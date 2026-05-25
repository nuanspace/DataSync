using System.Text;
using System.Text.Json;
using DataSync.CYYY.Models;
using Npgsql;

namespace DataSync.CYYY.Services;

/// <summary>
/// 本地查询服务，从采集表中按条件查询候选记录
/// </summary>
public class LocalQueryService
{
    private readonly string _connStr;
    private readonly ILogger<LocalQueryService> _logger;

    public LocalQueryService(
        IConfiguration configuration,
        ILogger<LocalQueryService> logger)
    {
        _connStr = configuration.GetConnectionString("SyncDb")
            ?? throw new InvalidOperationException("未找到连接字符串 'SyncDb'");
        _logger = logger;
    }

    /// <summary>
    /// 获取本地采集表的列名列表，排除系统列
    /// </summary>
    public async Task<List<string>> GetTableColumnsAsync(string localTableName, CancellationToken ct)
    {
        var parts = localTableName.Split('.', 2);
        var (schema, table) = parts.Length == 2
            ? (parts[0], parts[1])
            : ("public", parts[0]);

        const string sql = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
            ORDER BY ordinal_position
            """;

        var columns = new List<string>();
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var col = reader.GetString(0);
            if (!string.Equals(col, "ingested_at", StringComparison.OrdinalIgnoreCase))
                columns.Add(col);
        }

        return columns;
    }

    /// <summary>
    /// 查询本地表在指定时间范围内的原始记录数，不带业务过滤
    /// </summary>
    public async Task<int> CountRawRecordsAsync(
        string serverCode, string timeField, string from, string to, CancellationToken ct)
    {
        var tableName = IngestionService.GetLocalTableName(serverCode);
        var sql = $"""SELECT COUNT(*) FROM {tableName} WHERE "{timeField}" >= @from AND "{timeField}" <= @to""";

        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// 解析 TriggerConditions JSON，兼容新旧格式
    /// </summary>
    public TriggerConditionConfig ParseTriggerConditions(string? json, string? fallbackServerCode = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new TriggerConditionConfig();

        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("logic", out _))
                return JsonSerializer.Deserialize<TriggerConditionConfig>(json) ?? new TriggerConditionConfig();

            var config = new TriggerConditionConfig { Logic = "AND", ExcludeSynced = true };
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                    continue;

                var values = prop.Value.EnumerateArray()
                    .Select(v => v.GetString() ?? "")
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                if (values.Count == 0)
                    continue;

                config.Rules.Add(new ConditionRule
                {
                    Interface = fallbackServerCode ?? "",
                    Field = prop.Name,
                    Mode = "include",
                    Values = values
                });
            }

            return config;
        }
        catch
        {
            return new TriggerConditionConfig();
        }
    }

    /// <summary>
    /// 查询候选记录列表
    /// </summary>
    public async Task<List<Dictionary<string, object>>> QueryCandidatesAsync(
        SyncTask task,
        CancellationToken ct,
        string? scopeField = null,
        List<string>? scopeValues = null,
        string scopeOperator = "in",
        bool? excludeSyncedOverride = null,
        bool skipRules = false,
        Dictionary<string, string>? mainEqualsFilters = null,
        int? limit = null)
    {
        var config = ParseTriggerConditions(task.TriggerConditions, task.TriggerServerCode);

        if (config.Rules.Count == 0 && !skipRules)
        {
            if (!string.IsNullOrWhiteSpace(task.TriggerConditions))
            {
                _logger.LogWarning("任务 [{TaskName}] 过滤条件为空或无法解析", task.Name);
                return [];
            }

            _logger.LogInformation("任务 [{TaskName}] 未配置过滤条件，按触发源主表直接筛选", task.Name);
        }

        var mainServerCode = task.TriggerServerCode;
        var mainTable = IngestionService.GetLocalTableName(mainServerCode);
        var aliasMap = new Dictionary<string, (string tableName, string alias)>
        {
            [mainServerCode] = (mainTable, "t0")
        };
        var mainAlias = aliasMap[mainServerCode].alias;

        var sb = new StringBuilder();
        var parameters = new List<NpgsqlParameter>();
        var paramIdx = 0;

        if (skipRules && !string.IsNullOrEmpty(task.VisitSnField))
        {
            sb.AppendLine($"SELECT DISTINCT ON ({mainAlias}.\"{task.PatientIdField}\", {mainAlias}.\"{task.VisitSnField}\") t0.*");
        }
        else if (skipRules)
        {
            sb.AppendLine($"SELECT DISTINCT ON ({mainAlias}.\"{task.PatientIdField}\") t0.*");
        }
        else
        {
            sb.AppendLine("SELECT DISTINCT t0.*");
        }

        sb.AppendLine($"FROM {mainTable} {mainAlias}");

        if (!skipRules)
        {
            var aliasIdx = 1;
            foreach (var rule in config.Rules)
            {
                var serverCode = string.IsNullOrEmpty(rule.Interface) ? mainServerCode : rule.Interface;
                if (aliasMap.ContainsKey(serverCode))
                    continue;

                aliasMap[serverCode] = (IngestionService.GetLocalTableName(serverCode), $"t{aliasIdx++}");
            }

            var joinField = task.VisitSnField ?? "PAT_VISIT_SN";
            var skippedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (serverCode, (tableName, alias)) in aliasMap)
            {
                if (serverCode == mainServerCode)
                    continue;

                var columns = await GetTableColumnsAsync(tableName, ct);
                if (!columns.Contains(joinField, StringComparer.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "任务 [{TaskName}] 表 {Table} 不包含连接字段 [{JoinField}]，已跳过关联",
                        task.Name, tableName, joinField);
                    skippedCodes.Add(serverCode);
                    continue;
                }

                sb.AppendLine($"LEFT JOIN {tableName} {alias} ON {mainAlias}.\"{joinField}\" = {alias}.\"{joinField}\"");
            }

            foreach (var skippedCode in skippedCodes)
                aliasMap.Remove(skippedCode);
        }

        var whereClauses = new List<string>();
        if (!skipRules)
        {
            foreach (var rule in config.Rules)
            {
                var serverCode = string.IsNullOrEmpty(rule.Interface) ? mainServerCode : rule.Interface;
                if (!aliasMap.TryGetValue(serverCode, out var mapped))
                    continue;

                var fieldRef = $"{mapped.alias}.\"{rule.Field}\"";
                switch (rule.Mode)
                {
                    case "include" when rule.Values.Count > 0:
                    {
                        var paramName = $"@p{paramIdx++}";
                        whereClauses.Add($"{fieldRef} = ANY({paramName})");
                        parameters.Add(new NpgsqlParameter(paramName, rule.Values.ToArray()));
                        break;
                    }
                    case "exclude" when rule.Values.Count > 0:
                    {
                        var paramName = $"@p{paramIdx++}";
                        whereClauses.Add($"({fieldRef} IS NULL OR NOT ({fieldRef} = ANY({paramName})))");
                        parameters.Add(new NpgsqlParameter(paramName, rule.Values.ToArray()));
                        break;
                    }
                    case "not_null":
                        whereClauses.Add($"({fieldRef} IS NOT NULL AND {fieldRef} != '')");
                        break;
                }
            }
        }

        if (whereClauses.Count > 0)
        {
            var logic = string.Equals(config.Logic, "OR", StringComparison.OrdinalIgnoreCase) ? " OR " : " AND ";
            sb.AppendLine($"WHERE ({string.Join(logic, whereClauses)})");
        }
        else
        {
            sb.AppendLine("WHERE 1=1");
        }

        if (!string.IsNullOrEmpty(scopeField) && scopeValues is { Count: > 0 })
        {
            if (scopeOperator == "between" && scopeValues.Count >= 2)
            {
                const string pFrom = "@scopeFrom";
                const string pTo = "@scopeTo";
                sb.AppendLine($"AND {mainAlias}.\"{scopeField}\" >= {pFrom} AND {mainAlias}.\"{scopeField}\" <= {pTo}");
                parameters.Add(new NpgsqlParameter(pFrom, scopeValues[0]));
                parameters.Add(new NpgsqlParameter(pTo, scopeValues[1]));
            }
            else
            {
                const string pScope = "@scopeValues";
                sb.AppendLine($"AND {mainAlias}.\"{scopeField}\" = ANY({pScope})");
                parameters.Add(new NpgsqlParameter(pScope, scopeValues.ToArray()));
            }
        }

        if (mainEqualsFilters is { Count: > 0 })
        {
            foreach (var (field, value) in mainEqualsFilters)
            {
                var paramName = $"@pk{paramIdx++}";
                sb.AppendLine($"AND {mainAlias}.\"{field}\" = {paramName}");
                parameters.Add(new NpgsqlParameter(paramName, value));
            }
        }

        var excludeSynced = excludeSyncedOverride ?? config.ExcludeSynced;
        if (excludeSynced)
        {
            var patientMatch = $"psi.his_pat_id = COALESCE({mainAlias}.\"{task.PatientIdField}\"::text, '')";
            var visitMatch = string.IsNullOrWhiteSpace(task.VisitSnField)
                ? "psi.pat_visit_sn = ''"
                : $"psi.pat_visit_sn = COALESCE({mainAlias}.\"{task.VisitSnField}\"::text, '')";

            sb.AppendLine($"""
                AND NOT EXISTS (
                    SELECT 1 FROM cyyy.pending_sync_items psi
                    WHERE psi.task_code = @pendingTaskCode
                      AND psi.status = @pendingSuccessStatus
                      AND {patientMatch}
                      AND {visitMatch}
                )
                """);
            parameters.Add(new NpgsqlParameter("@pendingTaskCode", task.Code));
            parameters.Add(new NpgsqlParameter("@pendingSuccessStatus", PendingSyncStatuses.Success));
        }

        if (skipRules && !string.IsNullOrEmpty(task.VisitSnField))
            sb.AppendLine($"ORDER BY {mainAlias}.\"{task.PatientIdField}\", {mainAlias}.\"{task.VisitSnField}\"");
        else if (skipRules)
            sb.AppendLine($"ORDER BY {mainAlias}.\"{task.PatientIdField}\"");

        if (limit is > 0)
            sb.AppendLine($"LIMIT {limit.Value}");

        var sql = sb.ToString();
        _logger.LogDebug("任务 [{TaskName}] 动态 SQL:\n{Sql}", task.Name, sql);

        var result = new List<Dictionary<string, object>>();
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddRange(parameters.ToArray());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? "" : reader.GetValue(i);

            result.Add(row);
        }

        _logger.LogInformation("任务 [{TaskName}] 本地查询到 {Count} 条候选记录", task.Name, result.Count);
        return result;
    }

    /// <summary>
    /// 判断单条触发记录当前是否满足任务规则
    /// </summary>
    public async Task<bool> MatchesTriggerRecordAsync(
        SyncTask task,
        IngestionSource source,
        Dictionary<string, object> triggerRecord,
        CancellationToken ct)
    {
        var filters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var primaryKey in source.PrimaryKeyArray)
        {
            if (!triggerRecord.TryGetValue(primaryKey, out var value))
            {
                _logger.LogWarning(
                    "任务 [{TaskName}] 触发记录缺少主键字段 [{PrimaryKey}]，跳过登记",
                    task.Name, primaryKey);
                return false;
            }

            filters[primaryKey] = value switch
            {
                JsonElement element => element.ToString(),
                _ => value?.ToString() ?? ""
            };
        }

        var records = await QueryCandidatesAsync(
            task,
            ct,
            excludeSyncedOverride: false,
            mainEqualsFilters: filters,
            limit: 1);

        return records.Count > 0;
    }

    /// <summary>
    /// 按触发源主键查询当前满足规则的本地触发记录
    /// </summary>
    public async Task<Dictionary<string, object>?> QueryMatchingTriggerRecordAsync(
        SyncTask task,
        IngestionSource source,
        Dictionary<string, object> triggerRecord,
        CancellationToken ct)
    {
        var filters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var primaryKey in source.PrimaryKeyArray)
        {
            if (!triggerRecord.TryGetValue(primaryKey, out var value))
            {
                _logger.LogWarning(
                    "任务 [{TaskName}] 触发记录缺少主键字段 [{PrimaryKey}]，无法复核同步条件",
                    task.Name, primaryKey);
                return null;
            }

            filters[primaryKey] = value switch
            {
                JsonElement element => element.ToString(),
                _ => value?.ToString() ?? ""
            };
        }

        var records = await QueryCandidatesAsync(
            task,
            ct,
            excludeSyncedOverride: false,
            mainEqualsFilters: filters,
            limit: 1);

        return records.FirstOrDefault();
    }
}
