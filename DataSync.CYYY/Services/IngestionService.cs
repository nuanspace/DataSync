using System.Text.Json;
using DataSync.CYYY.Data;
using DataSync.CYYY.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataSync.CYYY.Services;

/// <summary>
/// 采集服务：从数据湖增量查询并 UPSERT 到本地采集表。
/// </summary>
public class IngestionService
{
    private const int CheckpointLookbackMinutes = 5;
    private const int UpsertBatchSize = 200;

    private readonly DataLakeClient _dataLakeClient;
    private readonly PendingSyncService _pendingSyncService;
    private readonly SyncTaskSignalService _syncTaskSignalService;
    private readonly SyncLogService _logService;
    private readonly IDbContextFactory<SyncDbContext> _dbFactory;
    private readonly ILogger<IngestionService> _logger;
    private readonly string _localConnStr;

    /// <summary>
    /// 已确认存在的本地表缓存，避免每次都查 information_schema。
    /// </summary>
    private static readonly HashSet<string> _existingTables = [];

    /// <summary>
    /// 已确认存在的列缓存，Key 为表名。
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> _existingColumns = [];

    /// <summary>
    /// 已确认存在的索引缓存，Key 为 table|column。
    /// </summary>
    private static readonly HashSet<string> _existingIndexes = [];

    public IngestionService(
        DataLakeClient dataLakeClient,
        PendingSyncService pendingSyncService,
        SyncTaskSignalService syncTaskSignalService,
        SyncLogService logService,
        IDbContextFactory<SyncDbContext> dbFactory,
        IConfiguration configuration,
        ILogger<IngestionService> logger)
    {
        _dataLakeClient = dataLakeClient;
        _pendingSyncService = pendingSyncService;
        _syncTaskSignalService = syncTaskSignalService;
        _logService = logService;
        _dbFactory = dbFactory;
        _logger = logger;
        _localConnStr = configuration.GetConnectionString("SyncDb")
            ?? throw new InvalidOperationException("未找到连接字符串 'SyncDb'");
    }

    /// <summary>
    /// 获取所有启用的采集源。
    /// </summary>
    public async Task<List<IngestionSource>> GetEnabledSourcesAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.IngestionSources.Where(s => s.Enabled).ToListAsync(ct);
    }

    /// <summary>
    /// 按 ServerCode 获取采集源。
    /// </summary>
    public async Task<IngestionSource?> GetSourceByServerCodeAsync(string serverCode, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.IngestionSources.FirstOrDefaultAsync(s => s.ServerCode == serverCode, ct);
    }

    /// <summary>
    /// 定时采集：按时间窗口查询。
    /// </summary>
    public async Task IngestAsync(IngestionSource source, CancellationToken ct)
    {
        var checkpointKey = $"INGEST_{source.ServerCode}";
        var to = DateTime.Now.AddMinutes(-source.EndOffsetMinutes);
        var fallbackFrom = DateTime.Now.AddMinutes(-source.StartOffsetMinutes);
        var checkpoint = await _logService.GetCheckpointAsync(checkpointKey, ct);
        var from = checkpoint.HasValue
            ? checkpoint.Value.AddMinutes(-CheckpointLookbackMinutes)
            : fallbackFrom;

        if (from > to)
            from = to;

        _logger.LogInformation(
            "采集 [{Name}] 本轮窗口：{From} ~ {To}（{Mode}）",
            source.Name,
            from,
            to,
            checkpoint.HasValue ? "检查点回看" : "默认时间窗");

        var conditions = new List<DataLakeCondition>
        {
            new() { Column = source.TimeField, Type = "ge", Value = from.ToString("yyyy-MM-dd HH:mm:ss") },
            new() { Column = source.TimeField, Type = "le", Value = to.ToString("yyyy-MM-dd HH:mm:ss") }
        };

        var count = await IngestCoreAsync(source, conditions, "Scheduled", from, to, ct);
        await _logService.UpdateCheckpointAsync(checkpointKey, to, ct, count);
    }

    /// <summary>
    /// 补录采集：调用方自行传入查询条件，不更新检查点。
    /// </summary>
    public async Task<int> IngestForBackfillAsync(
        IngestionSource source,
        List<DataLakeCondition> conditions,
        CancellationToken ct)
        => await IngestCoreAsync(source, conditions, "Backfill", null, null, ct);

    /// <summary>
    /// 核心采集流程：合并额外条件、查询数据湖、写本地表，并记录采集日志。
    /// </summary>
    private async Task<int> IngestCoreAsync(
        IngestionSource source,
        List<DataLakeCondition> conditions,
        string triggerType,
        DateTime? from,
        DateTime? to,
        CancellationToken ct)
    {
        var startedAt = DateTime.Now;
        var mergedConditions = new List<DataLakeCondition>(conditions);
        var conditionsJson = "[]";
        var apiCount = 0;
        var localCount = 0;
        var pageCount = 0;

        try
        {
            _logger.LogDebug("采集 [{Name}] 查询条件数 {Count}", source.Name, mergedConditions.Count);

            if (!string.IsNullOrWhiteSpace(source.Conditions))
            {
                var extra = JsonSerializer.Deserialize<List<DataLakeCondition>>(source.Conditions);
                if (extra?.Count > 0)
                    mergedConditions.AddRange(extra);
            }

            conditionsJson = JsonSerializer.Serialize(mergedConditions);
            _logger.LogDebug("采集 [{Name}] 查询条件: {Conditions}", source.Name, conditionsJson);

            var notifiedTaskCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queryResult = await _dataLakeClient.QueryPagesAsync(
                source.ServerCode,
                mergedConditions,
                async (records, currentPageNo) =>
                {
                    pageCount = currentPageNo;
                    await UpsertToLocalAsync(source, records, ct);

                    if (triggerType != "Scheduled")
                        return;

                    var taskCodes = await _pendingSyncService.EnqueueForIngestedRecordsAsync(source, records, ct);
                    foreach (var taskCode in taskCodes)
                        notifiedTaskCodes.Add(taskCode);
                },
                ct);

            apiCount = queryResult.TotalCount;
            pageCount = queryResult.PageCount;

            if (apiCount > 0)
            {
                if (notifiedTaskCodes.Count > 0)
                    _syncTaskSignalService.NotifyMany(notifiedTaskCodes);

                localCount = await GetLocalCountAsync(source.ServerCode, ct);
                _logger.LogInformation(
                    "采集 [{Name}] 完成，共 {PageCount} 页，API 返回 {ApiCount} 条，本地表现有 {LocalCount} 条",
                    source.Name, pageCount, apiCount, localCount);

                if (localCount == 0)
                    _logger.LogError("采集 [{Name}] 异常，UPSERT 后本地表仍为空", source.Name);
            }
            else
            {
                _logger.LogDebug("采集 [{Name}] 无数据", source.Name);
            }

            await AddIngestionLogAsync(new IngestionLog
            {
                ServerCode = source.ServerCode,
                SourceName = source.Name,
                TriggerType = triggerType,
                TimeField = source.TimeField,
                FromTime = from,
                ToTime = to,
                QueryConditions = conditionsJson,
                ApiCount = apiCount,
                LocalCount = localCount,
                Success = true,
                DurationMs = (long)(DateTime.Now - startedAt).TotalMilliseconds
            }, ct);

            return apiCount;
        }
        catch (Exception ex)
        {
            await AddIngestionLogAsync(new IngestionLog
            {
                ServerCode = source.ServerCode,
                SourceName = source.Name,
                TriggerType = triggerType,
                TimeField = source.TimeField,
                FromTime = from,
                ToTime = to,
                QueryConditions = conditionsJson,
                ApiCount = apiCount,
                LocalCount = localCount,
                Success = false,
                ErrorMessage = ex.Message,
                DurationMs = (long)(DateTime.Now - startedAt).TotalMilliseconds
            }, ct);

            throw;
        }
    }

    private async Task AddIngestionLogAsync(IngestionLog log, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.IngestionLogs.Add(log);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入采集日志失败，采集源 [{ServerCode}]", log.ServerCode);
        }
    }

    /// <summary>
    /// 根据 serverCode 自动生成本地采集表名。
    /// 规则：cyyy.dl_ + ServerCode 转小写并将 '-' 替换为 '_'
    /// </summary>
    public static string GetLocalTableName(string serverCode)
        => $"cyyy.dl_{serverCode.ToLower().Replace('-', '_')}";

    /// <summary>
    /// 获取本地采集表记录数。
    /// </summary>
    public async Task<int> GetLocalCountAsync(string serverCode, CancellationToken ct)
    {
        var tableName = GetLocalTableName(serverCode);

        await using var conn = new NpgsqlConnection(_localConnStr);
        await conn.OpenAsync(ct);

        if (!await TableExistsAsync(conn, tableName, ct))
            return 0;

        await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {tableName}", conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// UPSERT 到本地采集表；若表不存在则自动创建。
    /// </summary>
    private async Task UpsertToLocalAsync(
        IngestionSource source,
        IReadOnlyList<Dictionary<string, object>> records,
        CancellationToken ct)
    {
        if (records.Count == 0)
            return;

        var tableName = GetLocalTableName(source.ServerCode);
        var pkFields = source.PrimaryKeyArray;

        if (pkFields.Length == 0)
            throw new InvalidOperationException($"采集源 [{source.Name}] 未配置主键字段");

        await using var conn = new NpgsqlConnection(_localConnStr);
        await conn.OpenAsync(ct);

        var columnNames = CollectColumnNames(records);
        await EnsureTableExistsAsync(conn, tableName, columnNames, pkFields, ct);
        await EnsureColumnsExistAsync(conn, tableName, columnNames, ct);
        await EnsureIndexesAsync(conn, tableName, source, columnNames, ct);

        try
        {
            await ExecuteUpsertAsync(conn, tableName, records, pkFields, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            _logger.LogWarning("表 {Table} 不存在，清理缓存后重建", tableName);
            InvalidateTableCaches(tableName);

            await EnsureTableExistsAsync(conn, tableName, columnNames, pkFields, ct);
            await EnsureColumnsExistAsync(conn, tableName, columnNames, ct);
            await EnsureIndexesAsync(conn, tableName, source, columnNames, ct);
            await ExecuteUpsertAsync(conn, tableName, records, pkFields, ct);
        }
    }

    /// <summary>
    /// 执行 UPSERT。
    /// </summary>
    private async Task ExecuteUpsertAsync(
        NpgsqlConnection conn,
        string tableName,
        IReadOnlyList<Dictionary<string, object>> records,
        string[] pkFields,
        CancellationToken ct)
    {
        var pkSet = new HashSet<string>(pkFields, StringComparer.OrdinalIgnoreCase);
        var conflictColumns = string.Join(", ", pkFields.Select(k => $"\"{k}\""));

        var successCount = 0;
        var skippedCount = 0;
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            var pendingCommands = new List<NpgsqlBatchCommand>(UpsertBatchSize);
            foreach (var row in records)
            {
                ct.ThrowIfCancellationRequested();

                var missingPk = pkFields.FirstOrDefault(pk =>
                    !row.ContainsKey(pk)
                    || row[pk] is null
                    || row[pk] is JsonElement je && je.ValueKind == JsonValueKind.Null
                    || string.IsNullOrWhiteSpace(row[pk]?.ToString()));

                if (missingPk != null)
                {
                    skippedCount++;
                    _logger.LogWarning("跳过缺少主键 [{PkField}] 的数据行，表 {Table}", missingPk, tableName);
                    continue;
                }

                pendingCommands.Add(BuildUpsertBatchCommand(tableName, row, pkSet, conflictColumns));
                if (pendingCommands.Count < UpsertBatchSize)
                    continue;

                successCount += await ExecuteBatchAsync(conn, tx, pendingCommands, ct);
                pendingCommands.Clear();
            }

            if (pendingCommands.Count > 0)
                successCount += await ExecuteBatchAsync(conn, tx, pendingCommands, ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        if (skippedCount > 0)
            _logger.LogWarning("UPSERT 到 {Table} 时跳过 {Skipped} 行主键缺失记录", tableName, skippedCount);

        _logger.LogInformation("UPSERT 到 {Table} 完成：{Success}/{Total}", tableName, successCount, records.Count);
    }

    /// <summary>
    /// 检查本地表是否存在。
    /// </summary>
    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection conn,
        string fullTableName,
        CancellationToken ct)
    {
        var (schema, table) = ParseTableName(fullTableName);
        const string sql = "SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }

    /// <summary>
    /// 确保本地采集表存在，且主键与当前配置一致。
    /// </summary>
    private async Task EnsureTableExistsAsync(
        NpgsqlConnection conn,
        string fullTableName,
        IReadOnlyCollection<string> columnNames,
        string[] pkFields,
        CancellationToken ct)
    {
        var missingPk = pkFields.FirstOrDefault(pk => !columnNames.Contains(pk, StringComparer.OrdinalIgnoreCase));
        if (missingPk != null)
            throw new InvalidOperationException($"主键字段 [{missingPk}] 不在数据行中，请检查采集源配置");

        if (_existingTables.Contains(fullTableName))
            return;

        if (await TableExistsAsync(conn, fullTableName, ct))
        {
            var currentPks = await GetTablePrimaryKeysAsync(conn, fullTableName, ct);
            var configPkSet = new HashSet<string>(pkFields, StringComparer.OrdinalIgnoreCase);

            if (!currentPks.SetEquals(configPkSet))
            {
                _logger.LogWarning(
                    "表 {Table} 主键变更：{OldPks} -> {NewPks}，删除后重建",
                    fullTableName,
                    string.Join(",", currentPks),
                    string.Join(",", pkFields));

                await DropTableAsync(conn, fullTableName, ct);
            }
            else
            {
                _existingTables.Add(fullTableName);
                return;
            }
        }

        var (schema, _) = ParseTableName(fullTableName);
        var columnDefs = columnNames
            .Select(k => $"\"{k}\" TEXT")
            .ToList();
        var primaryKeyClause = string.Join(", ", pkFields.Select(k => $"\"{k}\""));

        var sql = $"""
            CREATE SCHEMA IF NOT EXISTS {schema};
            CREATE TABLE {fullTableName} (
                {string.Join(",\n                ", columnDefs)},
                ingested_at TIMESTAMPTZ DEFAULT NOW(),
                PRIMARY KEY ({primaryKeyClause})
            )
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);

        _existingTables.Add(fullTableName);
        _logger.LogInformation("自动创建本地采集表：{TableName}，主键：{PkFields}", fullTableName, string.Join(",", pkFields));
    }

    /// <summary>
    /// 动态补列：API 返回的新列若本地表不存在，则自动添加。
    /// </summary>
    private async Task EnsureColumnsExistAsync(
        NpgsqlConnection conn,
        string fullTableName,
        IReadOnlyCollection<string> columnNames,
        CancellationToken ct)
    {
        if (_existingColumns.TryGetValue(fullTableName, out var cachedCols)
            && columnNames.All(k => cachedCols.Contains(k)))
        {
            return;
        }

        var (schema, table) = ParseTableName(fullTableName);
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string querySql = "SELECT column_name FROM information_schema.columns WHERE table_schema = @s AND table_name = @t";

        await using (var cmd = new NpgsqlCommand(querySql, conn))
        {
            cmd.Parameters.AddWithValue("@s", schema);
            cmd.Parameters.AddWithValue("@t", table);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                existingCols.Add(reader.GetString(0));
        }

        foreach (var key in columnNames)
        {
            if (existingCols.Contains(key))
                continue;

            var alterSql = $"ALTER TABLE {fullTableName} ADD COLUMN \"{key}\" TEXT";
            await using var cmd = new NpgsqlCommand(alterSql, conn);
            await cmd.ExecuteNonQueryAsync(ct);

            existingCols.Add(key);
            _logger.LogInformation("动态补列：{Table}.\"{Column}\"", fullTableName, key);
        }

        _existingColumns[fullTableName] = existingCols;
    }

    /// <summary>
    /// 为常用查询列补索引，避免大表全表扫描。
    /// </summary>
    private async Task EnsureIndexesAsync(
        NpgsqlConnection conn,
        string fullTableName,
        IngestionSource source,
        IReadOnlyCollection<string> columnNames,
        CancellationToken ct)
    {
        var availableColumns = new HashSet<string>(columnNames, StringComparer.OrdinalIgnoreCase)
        {
            "ingested_at"
        };

        var targetColumns = new[]
        {
            source.TimeField,
            "PAT_VISIT_SN",
            "HIS_PAT_ID",
            "ingested_at"
        }
        .Where(c => !string.IsNullOrWhiteSpace(c) && availableColumns.Contains(c))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        foreach (var column in targetColumns)
        {
            var cacheKey = $"{fullTableName}|{column}";
            if (_existingIndexes.Contains(cacheKey))
                continue;

            var indexName = BuildIndexName(fullTableName, column);
            var sql = $"""CREATE INDEX IF NOT EXISTS "{indexName}" ON {fullTableName} ("{column}")""";
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);

            _existingIndexes.Add(cacheKey);
        }
    }

    private static List<string> CollectColumnNames(IReadOnlyList<Dictionary<string, object>> records)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in records)
        {
            foreach (var key in row.Keys)
            {
                if (seen.Add(key))
                    result.Add(key);
            }
        }

        return result;
    }

    private static NpgsqlBatchCommand BuildUpsertBatchCommand(
        string tableName,
        IReadOnlyDictionary<string, object> row,
        HashSet<string> pkSet,
        string conflictColumns)
    {
        var columns = row.Keys.Select(k => $"\"{k}\"").ToList();
        var paramNames = row.Keys.Select((_, i) => $"@p{i}").ToList();

        var updateSet = row.Keys
            .Where(k => !pkSet.Contains(k))
            .Select(k => $"\"{k}\" = EXCLUDED.\"{k}\"")
            .ToList();

        var updateClause = updateSet.Count > 0
            ? $"{string.Join(", ", updateSet)},\n                        ingested_at = NOW()"
            : "ingested_at = NOW()";

        var sql = $"""
            INSERT INTO {tableName} ({string.Join(", ", columns)}, ingested_at)
            VALUES ({string.Join(", ", paramNames)}, NOW())
            ON CONFLICT ({conflictColumns}) DO UPDATE SET
                {updateClause}
            """;

        var command = new NpgsqlBatchCommand(sql);
        var idx = 0;
        foreach (var value in row.Values)
        {
            var dbValue = value is JsonElement json ? json.ToString() : value;
            command.Parameters.AddWithValue($"@p{idx}", dbValue ?? DBNull.Value);
            idx++;
        }

        return command;
    }

    private static async Task<int> ExecuteBatchAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        IReadOnlyList<NpgsqlBatchCommand> commands,
        CancellationToken ct)
    {
        if (commands.Count == 0)
            return 0;

        await using var batch = new NpgsqlBatch(conn) { Transaction = tx };
        foreach (var command in commands)
            batch.BatchCommands.Add(command);

        return await batch.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 查询表当前主键列。
    /// </summary>
    private static async Task<HashSet<string>> GetTablePrimaryKeysAsync(
        NpgsqlConnection conn,
        string fullTableName,
        CancellationToken ct)
    {
        var (schema, table) = ParseTableName(fullTableName);
        const string sql = """
            SELECT a.attname
            FROM pg_index i
            JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
            WHERE i.indrelid = (SELECT oid FROM pg_class WHERE relname = @table
                AND relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = @schema))
            AND i.indisprimary
            """;

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));

        return result;
    }

    /// <summary>
    /// 删除采集表并清理缓存。
    /// </summary>
    private async Task DropTableAsync(NpgsqlConnection conn, string fullTableName, CancellationToken ct)
    {
        var sql = $"DROP TABLE IF EXISTS {fullTableName}";
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);

        InvalidateTableCaches(fullTableName);
        _logger.LogInformation("已删除采集表：{TableName}", fullTableName);
    }

    private static void InvalidateTableCaches(string fullTableName)
    {
        _existingTables.Remove(fullTableName);
        _existingColumns.Remove(fullTableName);

        var indexKeys = _existingIndexes
            .Where(key => key.StartsWith($"{fullTableName}|", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var indexKey in indexKeys)
            _existingIndexes.Remove(indexKey);
    }

    private static string BuildIndexName(string fullTableName, string columnName)
    {
        var (_, tableName) = ParseTableName(fullTableName);
        var normalizedTable = tableName.Replace('-', '_').ToLowerInvariant();
        var normalizedColumn = columnName.Replace('-', '_').ToLowerInvariant();
        var baseName = $"ix_{normalizedTable}_{normalizedColumn}";
        if (baseName.Length <= 63)
            return baseName;

        var hash = ComputeStableHash(baseName);
        return $"{baseName[..54]}_{hash}";
    }

    private static string ComputeStableHash(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in text)
            {
                hash ^= ch;
                hash *= 16777619;
            }

            return hash.ToString("x8");
        }
    }

    /// <summary>
    /// 解析 schema.table 格式的表名。
    /// </summary>
    private static (string schema, string table) ParseTableName(string fullTableName)
    {
        var parts = fullTableName.Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("public", parts[0]);
    }
}
