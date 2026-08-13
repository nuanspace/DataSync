using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    private readonly SyncOrchestrator _syncOrchestrator;
    private readonly PushServiceFactory _pushServiceFactory;
    private readonly ILogger<ActiveSyncService> _logger;

    public ActiveSyncService(
        IDbContextFactory<SyncDbContext> dbFactory,
        ActiveMedicalRecordClient activeClient,
        DatabaseQueryService databaseQueryService,
        SyncOrchestrator syncOrchestrator,
        PushServiceFactory pushServiceFactory,
        ILogger<ActiveSyncService> logger)
    {
        _dbFactory = dbFactory;
        _activeClient = activeClient;
        _databaseQueryService = databaseQueryService;
        _syncOrchestrator = syncOrchestrator;
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
            .Where(t => t.Enabled && (t.SyncTask == null || !t.SyncTask.PatientContinuousSyncEnabled))
            .OrderBy(t => t.Id)
            .ToListAsync(ct);
    }

    public async Task ExecuteTaskAsync(ActiveSyncTask task, CancellationToken ct)
    {
        var cases = new List<ActiveMedicalRecordInfo>();
        try
        {
            var batch = await _activeClient.GetActiveRecordsAsync(task, task.LastCursor, ct);
            if (batch.Items.Count == 0 && task.LastCursor.HasValue)
                batch = await _activeClient.GetActiveRecordsAsync(task, null, ct);

            await UpdateCursorAsync(task, batch.NextCursor, ct);
            var invalidCaseCount = batch.Items.Count(item => string.IsNullOrWhiteSpace(item.InpatientNo));
            cases = batch.Items
                .Where(item => !string.IsNullOrWhiteSpace(item.InpatientNo))
                .ToList();
            if (invalidCaseCount > 0)
            {
                await AddRunLogAsync(
                    task,
                    null,
                    null,
                    "Warning",
                    $"Active 病历中有 {invalidCaseCount} 条缺少 inpatientNo，未加入同步队列",
                    invalidCaseCount,
                    0,
                    0,
                    ct);
            }
            if (cases.Count == 0)
            {
                _logger.LogInformation("Active 补采任务 [{TaskName}] 未获取到 Active 病历", task.Name);
                await AddRunLogAsync(task, null, null, "Info", "未获取到 Active 病历", 0, 0, 0, ct);
            }
            else
            {
                await AddRunLogAsync(task, null, null, "Info", $"获取到 Active 病历 {cases.Count} 条", cases.Count, 0, 0, ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Active 病历列表获取失败，继续处理已入队病例");
            await AddRunLogAsync(task, null, null, "Error", $"Active 病历列表获取失败：{ex.Message}", 0, 0, 0, ct);
        }

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

            await QueueCasesAsync(task, source, cases, ct);

            if (source.SyncTaskInterface != null &&
                !InterfaceAccessWindow.IsOpen(source.SyncTaskInterface, DateTime.Now))
            {
                var nextOpen = InterfaceAccessWindow.GetNextOpen(source.SyncTaskInterface, DateTime.Now);
                await DeferPendingCasesAsync(task, source, nextOpen, ct);
                await AddRunLogAsync(
                    task,
                    source,
                    null,
                    "Info",
                    $"等待接口闲时窗口，下次执行时间 {nextOpen:yyyy-MM-dd HH:mm}",
                    cases.Count,
                    0,
                    0,
                    ct);
                continue;
            }

            var dueCases = await LoadDueCasesAsync(task, source, ct);
            if (dueCases.Count == 0)
            {
                await AddRunLogAsync(task, source, null, "Info", "本轮没有到期病历", cases.Count, 0, 0, ct);
                continue;
            }

            await ProcessSourceAsync(task, source, dueCases, ct);
        }
    }

    public async Task<List<ActiveMedicalRecordInfo>> GetCurrentActiveCasesAsync(
        int activeTaskId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var task = await db.ActiveSyncTasks
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == activeTaskId, ct);
        if (task == null)
            return [];

        var result = new List<ActiveMedicalRecordInfo>();
        var seenCursors = new HashSet<long>();
        long? cursor = null;
        while (true)
        {
            var batch = await _activeClient.GetActiveRecordsAsync(task, cursor, ct);
            result.AddRange(batch.Items);
            if (!batch.NextCursor.HasValue)
                break;
            if (!seenCursors.Add(batch.NextCursor.Value))
                throw new InvalidOperationException($"Active 病历接口返回了重复游标 {batch.NextCursor.Value}");

            cursor = batch.NextCursor;
        }

        return result
            .GroupBy(BuildActiveCaseKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private async Task QueueCasesAsync(
        ActiveSyncTask task,
        ActiveSyncSource source,
        List<ActiveMedicalRecordInfo> cases,
        CancellationToken ct)
    {
        if (cases.Count == 0)
            return;

        var now = DateTime.Now;
        var inpatientNos = cases.Select(c => c.InpatientNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var stateList = await db.ActiveSyncCaseSourceStates
            .Where(s => s.TaskId == task.Id &&
                s.SourceId == source.Id &&
                inpatientNos.Contains(s.InpatientNo))
            .ToListAsync(ct);
        var states = stateList.ToDictionary(s => s.InpatientNo, StringComparer.OrdinalIgnoreCase);

        foreach (var activeCase in cases)
        {
            if (!states.TryGetValue(activeCase.InpatientNo, out var state))
            {
                state = new ActiveSyncCaseSourceState
                {
                    TaskId = task.Id,
                    SourceId = source.Id,
                    InpatientNo = activeCase.InpatientNo,
                    NextQueryTime = now
                };
                db.ActiveSyncCaseSourceStates.Add(state);
                states[activeCase.InpatientNo] = state;
            }

            state.PendingCaseJson = JsonSerializer.Serialize(activeCase);
            state.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task DeferPendingCasesAsync(
        ActiveSyncTask task,
        ActiveSyncSource source,
        DateTime nextOpen,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var states = await db.ActiveSyncCaseSourceStates
            .Where(state => state.TaskId == task.Id &&
                state.SourceId == source.Id &&
                state.PendingCaseJson != null)
            .ToListAsync(ct);

        foreach (var state in states)
        {
            state.NextQueryTime = nextOpen;
            state.UpdatedAt = DateTime.Now;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<List<ActiveMedicalRecordInfo>> LoadDueCasesAsync(
        ActiveSyncTask task,
        ActiveSyncSource source,
        CancellationToken ct)
    {
        var now = DateTime.Now;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var snapshots = await db.ActiveSyncCaseSourceStates
            .AsNoTracking()
            .Where(state => state.TaskId == task.Id &&
                state.SourceId == source.Id &&
                state.PendingCaseJson != null &&
                (!state.NextQueryTime.HasValue || state.NextQueryTime <= now))
            .Select(state => state.PendingCaseJson!)
            .ToListAsync(ct);

        var result = new List<ActiveMedicalRecordInfo>();
        foreach (var snapshot in snapshots)
        {
            try
            {
                var activeCase = JsonSerializer.Deserialize<ActiveMedicalRecordInfo>(snapshot);
                if (activeCase != null)
                    result.Add(activeCase);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Active 病例待执行快照反序列化失败");
            }
        }

        return result;
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
            var records = await QuerySourceAsync(task, source, activeCase, ct);
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
        ActiveSyncTask task,
        ActiveSyncSource source,
        ActiveMedicalRecordInfo activeCase,
        CancellationToken ct)
    {
        if (source.SyncTaskInterface != null)
            return await QueryInterfaceSourceAsync(task, source.SyncTaskInterface, activeCase, ct);

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
        ActiveSyncTask task,
        SyncTaskInterface iface,
        ActiveMedicalRecordInfo activeCase,
        CancellationToken ct)
    {
        var syncTask = task.SyncTask
            ?? throw new InvalidOperationException($"Active 任务 [{task.Name}] 未关联同步任务");
        var patientId = ResolveActiveCaseValue(activeCase, task.PatientIdSource);
        var visitSn = ResolveActiveCaseValue(activeCase, task.VisitSnSource);
        if (string.IsNullOrWhiteSpace(patientId))
            throw new InvalidOperationException($"Active 病历缺少患者ID来源字段 [{task.PatientIdSource}]");
        if (!string.IsNullOrWhiteSpace(syncTask.VisitSnField) && string.IsNullOrWhiteSpace(visitSn))
            throw new InvalidOperationException($"Active 病历缺少就诊号来源字段 [{task.VisitSnSource}]");

        var triggerRecord = BuildActiveTriggerRecord(syncTask, activeCase, patientId, visitSn);
        return await _syncOrchestrator.QueryInterfaceForActiveAsync(iface, syncTask, triggerRecord, ct);
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
        if (string.IsNullOrWhiteSpace(error))
        {
            state.LastError = null;
            state.RetryCount = 0;
            state.PendingCaseJson = null;
            state.EmptyCount = resultCount == 0 ? state.EmptyCount + 1 : 0;
            state.NextQueryTime = now.AddSeconds(
                CalculateNextIntervalSeconds(task, source, state.EmptyCount, null));
        }
        else
        {
            state.RetryCount = 0;
            state.EmptyCount = 0;
            state.LastError = error;
            state.PendingCaseJson = null;
            state.NextQueryTime = now.AddSeconds(Math.Max(60, source.PollingIntervalSeconds));
        }
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

    private async Task UpdateCursorAsync(ActiveSyncTask task, long? nextCursor, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.ActiveSyncTasks
            .Where(item => item.Id == task.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.LastCursor, nextCursor), ct);
        task.LastCursor = nextCursor;
    }

    private static Dictionary<string, object> BuildActiveTriggerRecord(
        SyncTask syncTask,
        ActiveMedicalRecordInfo activeCase,
        string patientId,
        string visitSn)
    {
        var record = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            [syncTask.PatientIdField] = patientId
        };
        if (!string.IsNullOrWhiteSpace(syncTask.VisitSnField))
            record[syncTask.VisitSnField] = visitSn;

        TryAdd(record, "MRN", activeCase.Mrn);
        TryAdd(record, "INPATIENT_NO", activeCase.InpatientNo);
        TryAdd(record, "VISIT_NO", activeCase.VisitNo ?? "");
        TryAdd(record, "VISIT_ID", activeCase.VisitNo ?? "");
        TryAdd(record, "PATIENT_ID", activeCase.PatientId?.ToString() ?? "");
        TryAdd(record, "EVENT_ID", activeCase.EventId?.ToString() ?? "");
        if (activeCase.AdmissionTime.HasValue)
            TryAdd(record, "ADMISSION_TIME", activeCase.AdmissionTime.Value.ToString("yyyy-MM-dd HH:mm:ss"));
        return record;
    }

    internal static string ResolveActiveCaseValue(ActiveMedicalRecordInfo activeCase, string? source)
        => source?.Trim().ToLowerInvariant() switch
        {
            "mrn" => activeCase.Mrn,
            "inpatientno" or "inpatient_no" => activeCase.InpatientNo,
            "visitno" or "visit_no" => activeCase.VisitNo ?? "",
            "patientid" or "patient_id" => activeCase.PatientId?.ToString() ?? "",
            "eventid" or "event_id" => activeCase.EventId?.ToString() ?? "",
            _ => ""
        };

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

    private static string BuildSourceRecordKey(
        ActiveSyncSource source,
        IReadOnlyDictionary<string, object> record)
    {
        var keyFields = source.SourceRecordKeyArray;
        if (keyFields.Length == 0)
            return BuildRecordDigest(record);

        return string.Join("|", keyFields.Select(field =>
        {
            if (!TryGetValue(record, field, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"数据源 [{GetSourceDisplayName(source)}] 返回记录缺少唯一键字段 [{field}]");

            return $"{field}={EscapeKeyPart(value)}";
        }));
    }

    private static string BuildRecordDigest(IReadOnlyDictionary<string, object> record)
    {
        var normalized = string.Join("\n", record
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{item.Key.ToUpperInvariant()}={NormalizeDigestValue(item.Value)}"));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"SHA256:{Convert.ToHexString(digest)}";
    }

    private static string NormalizeDigestValue(object? value) => value switch
    {
        null => "<NULL>",
        JsonElement element => element.ToString(),
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? ""
    };

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

    private static string BuildActiveCaseKey(ActiveMedicalRecordInfo activeCase)
    {
        if (activeCase.Cursor > 0)
            return $"cursor:{activeCase.Cursor}";
        if (activeCase.EventId.HasValue)
            return $"event:{activeCase.EventId.Value}";

        return $"{activeCase.Mrn}|{activeCase.InpatientNo}|{activeCase.VisitNo}|{activeCase.AdmissionTime:O}";
    }

}
