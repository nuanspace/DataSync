using System.Text.Json;
using DataSync.CYYY.Data;
using DataSync.CYYY.Models;
using Microsoft.EntityFrameworkCore;

namespace DataSync.CYYY.Services;

/// <summary>
/// Active 病历补采执行服务。
/// </summary>
public class ActiveSyncService
{
    private readonly IDbContextFactory<SyncDbContext> _dbFactory;
    private readonly ActiveMedicalRecordClient _activeClient;
    private readonly DatabaseQueryService _databaseQueryService;
    private readonly PushServiceFactory _pushServiceFactory;
    private readonly ILogger<ActiveSyncService> _logger;

    public ActiveSyncService(
        IDbContextFactory<SyncDbContext> dbFactory,
        ActiveMedicalRecordClient activeClient,
        DatabaseQueryService databaseQueryService,
        PushServiceFactory pushServiceFactory,
        ILogger<ActiveSyncService> logger)
    {
        _dbFactory = dbFactory;
        _activeClient = activeClient;
        _databaseQueryService = databaseQueryService;
        _pushServiceFactory = pushServiceFactory;
        _logger = logger;
    }

    public async Task<List<ActiveSyncTask>> GetEnabledTasksAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ActiveSyncTasks
            .AsNoTracking()
            .Include(t => t.SyncTask)
            .Include(t => t.Sources.OrderBy(s => s.SortOrder))
                .ThenInclude(s => s.SyncTaskInterface)
                    .ThenInclude(i => i!.DatabaseResource)
            .Include(t => t.Sources)
                .ThenInclude(s => s.DatabaseResource)
            .Where(t => t.Enabled)
            .OrderBy(t => t.Id)
            .ToListAsync(ct);
    }

    public async Task ExecuteTaskAsync(ActiveSyncTask task, CancellationToken ct)
    {
        var cases = await _activeClient.GetActiveRecordsAsync(task, ct);
        if (cases.Count == 0)
        {
            _logger.LogInformation("Active 补采任务 [{TaskName}] 未获取到 Active 病历", task.Name);
            await AddRunLogAsync(task, null, null, "Info", "未获取到 Active 病历", 0, 0, 0, ct);
            return;
        }

        await AddRunLogAsync(task, null, null, "Info", $"获取到 Active 病历 {cases.Count} 条", cases.Count, 0, 0, ct);

        var sources = task.Sources
            .Where(s => s.Enabled && (s.SyncTaskInterface == null || s.SyncTaskInterface.Enabled))
            .OrderBy(s => s.SortOrder)
            .ToList();
        if (sources.Count == 0)
        {
            _logger.LogWarning("Active 补采任务 [{TaskName}] 未配置启用的数据源", task.Name);
            await AddRunLogAsync(task, null, null, "Warning", "未配置启用的补采接口", cases.Count, 0, 0, ct);
            return;
        }

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();

            var dueCases = await FilterDueCasesAsync(task, source, cases, ct);
            if (dueCases.Count == 0)
            {
                await AddRunLogAsync(task, source, null, "Info", "本轮没有到期病历", cases.Count, 0, 0, ct);
                continue;
            }

            await ProcessSourceAsync(task, source, dueCases, ct);
        }
    }

    private async Task<List<ActiveMedicalRecordInfo>> FilterDueCasesAsync(
        ActiveSyncTask task,
        ActiveSyncSource source,
        List<ActiveMedicalRecordInfo> cases,
        CancellationToken ct)
    {
        var now = DateTime.Now;
        var inpatientNos = cases.Select(c => c.InpatientNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var stateList = await db.ActiveSyncCaseSourceStates
            .AsNoTracking()
            .Where(s => s.TaskId == task.Id &&
                s.SourceId == source.Id &&
                inpatientNos.Contains(s.InpatientNo))
            .ToListAsync(ct);
        var states = stateList.ToDictionary(s => s.InpatientNo, StringComparer.OrdinalIgnoreCase);

        return cases
            .Where(c => !states.TryGetValue(c.InpatientNo, out var state) ||
                !state.NextQueryTime.HasValue ||
                state.NextQueryTime <= now)
            .ToList();
    }

    private async Task ProcessSourceAsync(
        ActiveSyncTask task,
        ActiveSyncSource source,
        List<ActiveMedicalRecordInfo> cases,
        CancellationToken ct)
    {
        var concurrency = Math.Max(1, task.Concurrency);
        using var semaphore = new SemaphoreSlim(concurrency);
        var jobs = cases.Select(async activeCase =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                await ProcessCaseSourceAsync(task, source, activeCase, ct);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(jobs);
    }

    private async Task ProcessCaseSourceAsync(
        ActiveSyncTask task,
        ActiveSyncSource source,
        ActiveMedicalRecordInfo activeCase,
        CancellationToken ct)
    {
        try
        {
            var records = await QuerySourceAsync(source, activeCase, ct);
            if (records.Count > 0)
                InjectActiveCaseFields(records, activeCase);

            var newRecords = await FilterNewRecordsAsync(task, source, activeCase.InpatientNo, records, ct);
            if (newRecords.Count > 0)
            {
                var pushTarget = ResolvePushTarget(task);
                var pushService = _pushServiceFactory.GetPushService(ResolvePushType(task));
                await pushService.PushAsync(
                    ResolveInterfacePushTarget(pushTarget, source.SyncTaskInterface?.PushParams),
                    GetSourceServerCode(source),
                    newRecords,
                    ct);
                await SaveReceiptsAsync(task, source, activeCase.InpatientNo, newRecords, ct);
            }

            await UpdateStateAsync(task, source, activeCase.InpatientNo, records.Count, null, ct);
            await AddRunLogAsync(
                task,
                source,
                activeCase.InpatientNo,
                "Info",
                $"查询 {records.Count} 条，新推送 {newRecords.Count} 条",
                1,
                records.Count,
                newRecords.Count,
                ct);

            _logger.LogInformation(
                "Active 补采 [{TaskName}] 数据源 [{SourceName}] 住院号 {InpatientNo} 查询 {Count} 条，新推送 {NewCount} 条",
                task.Name,
                GetSourceDisplayName(source),
                activeCase.InpatientNo,
                records.Count,
                newRecords.Count);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await UpdateStateAsync(task, source, activeCase.InpatientNo, 0, ex.Message, ct);
            await AddRunLogAsync(task, source, activeCase.InpatientNo, "Error", ex.Message, 1, 0, 0, ct);
            _logger.LogError(
                ex,
                "Active 补采 [{TaskName}] 数据源 [{SourceName}] 住院号 {InpatientNo} 处理失败",
                task.Name,
                GetSourceDisplayName(source),
                activeCase.InpatientNo);
        }
    }

    private async Task<List<Dictionary<string, object>>> QuerySourceAsync(
        ActiveSyncSource source,
        ActiveMedicalRecordInfo activeCase,
        CancellationToken ct)
    {
        if (source.SyncTaskInterface != null)
            return await QueryInterfaceSourceAsync(source, source.SyncTaskInterface, activeCase, ct);

        if (source.DatabaseResource == null)
            throw new InvalidOperationException($"数据源 [{source.DisplayName}] 未配置数据库资源");

        var resource = source.DatabaseResource!;
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [NormalizeParameterName(source.InpatientNoParameter)] = activeCase.InpatientNo
        };

        if (!string.IsNullOrWhiteSpace(source.AdmissionTimeParameter) &&
            activeCase.AdmissionTime.HasValue)
        {
            parameters[NormalizeParameterName(source.AdmissionTimeParameter)] = activeCase.AdmissionTime.Value;
        }

        return await _databaseQueryService.QueryByNamedParametersAsync(
            resource.DatabaseType,
            null,
            resource.Host,
            resource.DatabaseName,
            resource.Username,
            resource.Password,
            resource.TrustCertificate,
            source.QuerySql,
            parameters,
            ct);
    }

    private async Task<List<Dictionary<string, object>>> QueryInterfaceSourceAsync(
        ActiveSyncSource source,
        SyncTaskInterface iface,
        ActiveMedicalRecordInfo activeCase,
        CancellationToken ct)
    {
        if (!IngestionService.IsDatabaseSourceType(iface.SourceType))
            throw new InvalidOperationException($"接口 [{GetInterfaceDisplayName(iface)}] 不是数据库接口，Active 补采暂不支持");

        if (string.IsNullOrWhiteSpace(iface.QuerySql))
            throw new InvalidOperationException($"接口 [{GetInterfaceDisplayName(iface)}] 未配置查询 SQL");

        if (string.IsNullOrWhiteSpace(iface.QueryField))
            throw new InvalidOperationException($"接口 [{GetInterfaceDisplayName(iface)}] 未配置查询字段");

        var queryValue = ResolveActiveQueryValue(source, activeCase);
        if (string.IsNullOrWhiteSpace(queryValue))
            return [];

        var connection = ResolveDatabaseConnection(source, iface);
        return await _databaseQueryService.QueryByValuesAsync(
            connection.DatabaseType,
            connection.ConnectionStringName,
            connection.Host,
            connection.Database,
            connection.Username,
            connection.Password,
            connection.TrustCertificate,
            iface.QuerySql,
            iface.QueryField,
            [queryValue],
            ct);
    }

    private static DatabaseConnectionConfig ResolveDatabaseConnection(
        ActiveSyncSource source,
        SyncTaskInterface iface)
    {
        if (iface.DatabaseResource != null)
            return ToDatabaseConnectionConfig(iface.DatabaseResource);

        if (source.DatabaseResource != null)
            return ToDatabaseConnectionConfig(source.DatabaseResource);

        var databaseType = IngestionService.NormalizeDatabaseType(iface.DatabaseType, iface.SourceType);
        return new DatabaseConnectionConfig(
            databaseType,
            iface.ConnectionStringName,
            iface.SqlServerHost,
            iface.SqlServerDatabase,
            iface.SqlServerUsername,
            iface.SqlServerPassword,
            iface.SqlServerTrustCertificate);
    }

    private static DatabaseConnectionConfig ToDatabaseConnectionConfig(DatabaseResource resource) => new(
        resource.DatabaseType,
        null,
        resource.Host,
        resource.DatabaseName,
        resource.Username,
        resource.Password,
        resource.TrustCertificate);

    private static string ResolveActiveQueryValue(
        ActiveSyncSource source,
        ActiveMedicalRecordInfo activeCase)
    {
        var valueSource = source.InpatientNoParameter;
        if (string.IsNullOrWhiteSpace(valueSource))
            valueSource = "InpatientNo";

        return valueSource.Trim().ToLowerInvariant() switch
        {
            "mrn" or "medicalrecordnumber" or "medical_record_number" => activeCase.Mrn,
            "visitno" or "visit_no" => activeCase.VisitNo ?? "",
            _ => activeCase.InpatientNo
        };
    }

    private async Task<List<Dictionary<string, object>>> FilterNewRecordsAsync(
        ActiveSyncTask task,
        ActiveSyncSource source,
        string inpatientNo,
        List<Dictionary<string, object>> records,
        CancellationToken ct)
    {
        if (records.Count == 0)
            return [];

        var keys = records
            .Select(record => BuildSourceRecordKey(source, record))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existingKeys = await db.ActiveSyncRecordReceipts
            .AsNoTracking()
            .Where(r => r.TaskId == task.Id &&
                r.SourceId == source.Id &&
                r.InpatientNo == inpatientNo &&
                keys.Contains(r.SourceRecordKey))
            .Select(r => r.SourceRecordKey)
            .ToListAsync(ct);
        var existingSet = existingKeys.ToHashSet(StringComparer.Ordinal);

        return records
            .Where(record => !existingSet.Contains(BuildSourceRecordKey(source, record)))
            .ToList();
    }

    private async Task SaveReceiptsAsync(
        ActiveSyncTask task,
        ActiveSyncSource source,
        string inpatientNo,
        List<Dictionary<string, object>> pushedRecords,
        CancellationToken ct)
    {
        var now = DateTime.Now;
        var receipts = pushedRecords
            .Select(record => BuildSourceRecordKey(source, record))
            .Distinct(StringComparer.Ordinal)
            .Select(key => new ActiveSyncRecordReceipt
            {
                TaskId = task.Id,
                SourceId = source.Id,
                InpatientNo = inpatientNo,
                SourceRecordKey = key,
                PushedAt = now
            })
            .ToList();

        if (receipts.Count == 0)
            return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.ActiveSyncRecordReceipts.AddRange(receipts);
        await db.SaveChangesAsync(ct);
    }

    private async Task AddRunLogAsync(
        ActiveSyncTask task,
        ActiveSyncSource? source,
        string? inpatientNo,
        string level,
        string message,
        int activeCaseCount,
        int queryCount,
        int pushedCount,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.ActiveSyncRunLogs.Add(new ActiveSyncRunLog
        {
            TaskId = task.Id,
            SourceId = source?.Id,
            TaskName = task.Name,
            SourceName = source == null ? null : GetSourceDisplayName(source),
            InpatientNo = string.IsNullOrWhiteSpace(inpatientNo) ? null : inpatientNo,
            Level = level,
            Message = message,
            ActiveCaseCount = activeCaseCount,
            QueryCount = queryCount,
            PushedCount = pushedCount,
            CreatedAt = DateTime.Now
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task UpdateStateAsync(
        ActiveSyncTask task,
        ActiveSyncSource source,
        string inpatientNo,
        int resultCount,
        string? error,
        CancellationToken ct)
    {
        var now = DateTime.Now;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var state = await db.ActiveSyncCaseSourceStates.FirstOrDefaultAsync(s =>
            s.TaskId == task.Id &&
            s.SourceId == source.Id &&
            s.InpatientNo == inpatientNo,
            ct);

        if (state == null)
        {
            state = new ActiveSyncCaseSourceState
            {
                TaskId = task.Id,
                SourceId = source.Id,
                InpatientNo = inpatientNo
            };
            db.ActiveSyncCaseSourceStates.Add(state);
        }

        state.LastQueryAt = now;
        state.LastResultCount = resultCount;
        state.LastError = error;
        state.EmptyCount = string.IsNullOrWhiteSpace(error) && resultCount == 0 ? state.EmptyCount + 1 : 0;
        state.NextQueryTime = now.AddSeconds(CalculateNextIntervalSeconds(task, source, state.EmptyCount, error));
        state.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
    }

    private static int CalculateNextIntervalSeconds(
        ActiveSyncTask task,
        ActiveSyncSource source,
        int emptyCount,
        string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
            return Math.Max(60, task.PollingIntervalSeconds);

        if (emptyCount <= 0)
            return Math.Max(60, source.PollingIntervalSeconds);

        var baseSeconds = Math.Max(60, task.EmptyBackoffBaseSeconds);
        var maxSeconds = Math.Max(baseSeconds, task.EmptyBackoffMaxSeconds);
        var factor = Math.Min(emptyCount, 6);
        var seconds = baseSeconds * factor;
        return Math.Min(seconds, maxSeconds);
    }

    private static void InjectActiveCaseFields(
        List<Dictionary<string, object>> records,
        ActiveMedicalRecordInfo activeCase)
    {
        foreach (var record in records)
        {
            TryAdd(record, "MRN", activeCase.Mrn);
            TryAdd(record, "INPATIENT_NO", activeCase.InpatientNo);
            TryAdd(record, "VISIT_NO", activeCase.VisitNo ?? "");
            if (activeCase.AdmissionTime.HasValue)
                TryAdd(record, "ADMISSION_TIME", activeCase.AdmissionTime.Value.ToString("yyyy-MM-dd HH:mm:ss"));
        }
    }

    private static void TryAdd(Dictionary<string, object> record, string key, object value)
    {
        if (!record.ContainsKey(key))
            record[key] = value;
    }

    private static string ResolvePushType(ActiveSyncTask task)
        => string.IsNullOrWhiteSpace(task.SyncTask?.PushType) ? task.PushType : task.SyncTask.PushType;

    private static string ResolvePushTarget(ActiveSyncTask task)
    {
        var pushTarget = string.IsNullOrWhiteSpace(task.SyncTask?.PushTarget)
            ? task.PushTarget
            : task.SyncTask.PushTarget;

        if (string.IsNullOrWhiteSpace(pushTarget))
            throw new InvalidOperationException($"Active 补采任务 [{task.Name}] 未配置推送目标");

        return pushTarget;
    }

    private string ResolveInterfacePushTarget(string pushTarget, string? pushParams)
    {
        if (string.IsNullOrWhiteSpace(pushParams) || !pushTarget.Contains('{', StringComparison.Ordinal))
            return pushTarget;

        try
        {
            var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(pushParams);
            if (parameters == null)
                return pushTarget;

            var resolved = pushTarget;
            foreach (var (key, value) in parameters)
                resolved = resolved.Replace($"{{{key}}}", value, StringComparison.OrdinalIgnoreCase);

            return resolved;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Active 补采接口推送路由参数解析失败：{PushParams}", pushParams);
            return pushTarget;
        }
    }

    private static string GetSourceServerCode(ActiveSyncSource source)
        => string.IsNullOrWhiteSpace(source.SyncTaskInterface?.ServerCode)
            ? source.ServerCode
            : source.SyncTaskInterface.ServerCode;

    private static string GetSourceDisplayName(ActiveSyncSource source)
        => string.IsNullOrWhiteSpace(source.SyncTaskInterface?.DisplayName)
            ? source.DisplayName
            : source.SyncTaskInterface.DisplayName;

    private static string GetInterfaceDisplayName(SyncTaskInterface iface)
        => string.IsNullOrWhiteSpace(iface.DisplayName)
            ? iface.ServerCode
            : $"{iface.DisplayName}（{iface.ServerCode}）";

    private static string BuildSourceRecordKey(
        ActiveSyncSource source,
        IReadOnlyDictionary<string, object> record)
    {
        var keyFields = source.SourceRecordKeyArray;
        if (keyFields.Length == 0)
            throw new InvalidOperationException($"数据源 [{GetSourceDisplayName(source)}] 未配置源记录唯一键字段");

        return string.Join("|", keyFields.Select(field =>
        {
            if (!TryGetValue(record, field, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"数据源 [{GetSourceDisplayName(source)}] 返回记录缺少唯一键字段 [{field}]");

            return $"{field}={EscapeKeyPart(value)}";
        }));
    }

    private static bool TryGetValue(
        IReadOnlyDictionary<string, object> record,
        string field,
        out string value)
    {
        foreach (var (key, itemValue) in record)
        {
            if (!string.Equals(key, field, StringComparison.OrdinalIgnoreCase))
                continue;

            value = itemValue switch
            {
                JsonElement element => element.ToString(),
                _ => itemValue?.ToString() ?? ""
            };
            return true;
        }

        value = "";
        return false;
    }

    private static string NormalizeParameterName(string name)
        => name.Trim().TrimStart('@', ':');

    private static string EscapeKeyPart(string value)
        => value.Replace("\\", "\\\\").Replace("|", "\\|").Replace("=", "\\=");

    private sealed record DatabaseConnectionConfig(
        string DatabaseType,
        string? ConnectionStringName,
        string? Host,
        string? Database,
        string? Username,
        string? Password,
        bool TrustCertificate);
}
