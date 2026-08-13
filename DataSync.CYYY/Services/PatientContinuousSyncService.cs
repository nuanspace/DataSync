using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataSync.CYYY.Data;
using DataSync.CYYY.Models;
using Microsoft.EntityFrameworkCore;

namespace DataSync.CYYY.Services;

/// <summary>
/// 按患者、接口水位持续查询并仅推送未见过的新记录。
/// </summary>
public class PatientContinuousSyncService
{
    private readonly IDbContextFactory<SyncDbContext> _dbFactory;
    private readonly SyncOrchestrator _syncOrchestrator;
    private readonly ILogger<PatientContinuousSyncService> _logger;

    public PatientContinuousSyncService(
        IDbContextFactory<SyncDbContext> dbFactory,
        SyncOrchestrator syncOrchestrator,
        ILogger<PatientContinuousSyncService> logger)
    {
        _dbFactory = dbFactory;
        _syncOrchestrator = syncOrchestrator;
        _logger = logger;
    }

    public async Task<List<SyncTask>> GetEnabledTasksAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SyncTasks
            .AsNoTracking()
            .Include(task => task.Interfaces)
            .Where(task => task.Enabled && task.PatientContinuousSyncEnabled)
            .OrderBy(task => task.Id)
            .ToListAsync(ct);
    }

    public async Task ExecuteTaskAsync(SyncTask task, CancellationToken ct)
    {
        var now = DateTime.Now;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sessionIds = await db.PatientContinuousSyncSessions
            .AsNoTracking()
            .Where(item => item.TaskId == task.Id && item.NextRunAt <= now &&
                (item.Status == PatientContinuousSyncStatuses.Active ||
                 item.Status == PatientContinuousSyncStatuses.Closing))
            .OrderBy(item => item.NextRunAt)
            .Take(Math.Max(1, task.PatientConcurrency) * 20)
            .Select(item => item.Id)
            .ToListAsync(ct);
        if (sessionIds.Count == 0)
            return;

        var metrics = new ExecutionMetrics();
        using var patientSemaphore = new SemaphoreSlim(Math.Max(1, task.PatientConcurrency));
        using var apiSemaphore = new SemaphoreSlim(Math.Max(1, task.ApiConcurrency));
        var jobs = sessionIds.Select(async sessionId =>
        {
            await patientSemaphore.WaitAsync(ct);
            try
            {
                await ProcessSessionAsync(task, sessionId, apiSemaphore, metrics, ct);
            }
            finally
            {
                patientSemaphore.Release();
            }
        });
        await Task.WhenAll(jobs);

        await AddRunLogSafelyAsync(new PatientContinuousSyncRunLog
        {
            TaskId = task.Id,
            Level = metrics.FailedCount > 0 ? "Warning" : "Info",
            Message = $"本批处理患者 {sessionIds.Count} 个，查询 {metrics.QueryCount} 条，推送 {metrics.PushedCount} 条，失败 {metrics.FailedCount} 个接口",
            QueryCount = metrics.QueryCount,
            PushedCount = metrics.PushedCount,
            FailedCount = metrics.FailedCount
        }, ct);
    }

    private async Task ProcessSessionAsync(
        SyncTask task,
        long sessionId,
        SemaphoreSlim apiSemaphore,
        ExecutionMetrics metrics,
        CancellationToken ct)
    {
        PatientContinuousSyncSession? session;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            session = await db.PatientContinuousSyncSessions.FirstOrDefaultAsync(item => item.Id == sessionId, ct);
            if (session == null || session.AdmissionTime == null)
                return;

            await EnsureInterfaceStatesAsync(db, task, session, ct);
            await db.SaveChangesAsync(ct);
        }

        var interfaces = task.Interfaces
            .Where(IsEnabledContinuousApi)
            .OrderBy(item => item.SortOrder)
            .ToList();
        if (interfaces.Count == 0)
        {
            const string error = "未配置可执行的患者持续同步 API 接口";
            await UpdateSessionErrorAsync(sessionId, error, task, ct);
            metrics.AddFailure();
            await AddRunLogSafelyAsync(new PatientContinuousSyncRunLog
            {
                TaskId = task.Id,
                SessionId = session.Id,
                PatientId = session.PatientId,
                VisitSn = session.VisitSn,
                Level = "Error",
                Message = error,
                FailedCount = 1
            }, ct);
            return;
        }

        var now = DateTime.Now;
        List<int> dueStateInterfaceIds;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var interfaceIds = interfaces.Select(item => item.Id).ToList();
            dueStateInterfaceIds = await db.PatientContinuousSyncInterfaceStates
                .AsNoTracking()
                .Where(item => item.SessionId == sessionId &&
                    interfaceIds.Contains(item.InterfaceId) &&
                    item.Status != PatientContinuousInterfaceStatuses.Completed &&
                    item.NextRunAt <= now)
                .Select(item => item.InterfaceId)
                .ToListAsync(ct);
        }

        var interfaceMap = interfaces.ToDictionary(item => item.Id);
        var jobs = dueStateInterfaceIds
            .Where(interfaceMap.ContainsKey)
            .Select(async interfaceId =>
            {
                await apiSemaphore.WaitAsync(ct);
                try
                {
                    await ProcessInterfaceAsync(task, session, interfaceMap[interfaceId], metrics, ct);
                }
                finally
                {
                    apiSemaphore.Release();
                }
            });
        await Task.WhenAll(jobs);
        await RefreshSessionStateAsync(task, sessionId, interfaces.Select(item => item.Id).ToList(), ct);
    }

    private async Task ProcessInterfaceAsync(
        SyncTask task,
        PatientContinuousSyncSession session,
        SyncTaskInterface iface,
        ExecutionMetrics metrics,
        CancellationToken ct)
    {
        var now = DateTime.Now;
        if (!InterfaceAccessWindow.IsOpen(iface, now))
        {
            var nextOpen = InterfaceAccessWindow.GetNextOpen(iface, now);
            await MarkInterfaceWaitingAsync(session.Id, iface.Id, nextOpen, "等待接口闲时窗口", false, ct);
            return;
        }

        PatientContinuousSyncInterfaceState state;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            state = await db.PatientContinuousSyncInterfaceStates.FirstAsync(item =>
                item.SessionId == session.Id && item.InterfaceId == iface.Id, ct);
            state.Status = PatientContinuousInterfaceStatuses.Running;
            state.LastStartedAt = now;
            await db.SaveChangesAsync(ct);
        }

        var queriedCount = 0;
        DateTime? queryFrom = null;
        DateTime? queryTo = null;
        try
        {
            var triggerRecord = DeserializeRecord(session.TriggerRecordJson);
            var watermarkTo = session.Status == PatientContinuousSyncStatuses.Closing && session.DischargeTime.HasValue
                ? session.DischargeTime.Value
                : now;
            var admissionTime = session.AdmissionTime
                ?? throw new InvalidOperationException("患者持续同步档案缺少入院时间");
            if (iface.ContinuousUseTimeRange)
            {
                var from = state.Watermark.HasValue
                    ? state.Watermark.Value.AddMinutes(-Math.Max(0, task.PatientContinuousSyncLookbackMinutes))
                    : admissionTime;
                if (from < admissionTime)
                    from = admissionTime;
                if (from > watermarkTo)
                    from = watermarkTo;
                queryFrom = from;
                queryTo = watermarkTo;
            }

            var records = await _syncOrchestrator.QueryInterfaceForPatientContinuousAsync(
                iface, task, triggerRecord, queryFrom, queryTo, ct);
            queriedCount = records.Count;
            var keyedRecords = records
                .Select(record => new { Record = record, Key = BuildRecordKey(iface, record) })
                .GroupBy(item => item.Key, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            HashSet<string> existingKeys;
            await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            {
                var keys = keyedRecords.Select(item => item.Key).ToList();
                var existing = keys.Count == 0
                    ? []
                    : await db.PatientContinuousSyncReceipts
                        .AsNoTracking()
                        .Where(item => item.SessionId == session.Id &&
                            item.InterfaceId == iface.Id && keys.Contains(item.RecordKey))
                        .Select(item => item.RecordKey)
                        .ToListAsync(ct);
                existingKeys = existing.ToHashSet(StringComparer.Ordinal);
            }

            var newItems = keyedRecords.Where(item => !existingKeys.Contains(item.Key)).ToList();
            var newRecords = newItems.Select(item => item.Record).ToList();
            if (newRecords.Count > 0)
            {
                _syncOrchestrator.InjectPatientContinuousFields(newRecords, triggerRecord, iface);
                await _syncOrchestrator.PushPatientContinuousDataAsync(task, iface, newRecords, ct);
            }

            await using var saveDb = await _dbFactory.CreateDbContextAsync(ct);
            if (newItems.Count > 0)
            {
                saveDb.PatientContinuousSyncReceipts.AddRange(newItems.Select(item =>
                    new PatientContinuousSyncReceipt
                    {
                        SessionId = session.Id,
                        InterfaceId = iface.Id,
                        RecordKey = item.Key,
                        PushedAt = DateTime.Now
                    }));
            }

            var savedState = await saveDb.PatientContinuousSyncInterfaceStates.FirstAsync(item =>
                item.SessionId == session.Id && item.InterfaceId == iface.Id, ct);
            savedState.Watermark = watermarkTo;
            savedState.LastSuccessAt = DateTime.Now;
            savedState.LastError = null;
            savedState.RetryCount = 0;
            savedState.Status = session.Status == PatientContinuousSyncStatuses.Closing
                ? PatientContinuousInterfaceStatuses.Completed
                : PatientContinuousInterfaceStatuses.Pending;
            savedState.NextRunAt = session.Status == PatientContinuousSyncStatuses.Closing
                ? DateTime.MaxValue
                : DateTime.Now.AddSeconds(Math.Max(60, task.PatientContinuousSyncIntervalSeconds));
            await saveDb.SaveChangesAsync(ct);
            metrics.AddSuccess(records.Count, newRecords.Count);

            if (newRecords.Count > 0)
            {
                await AddRunLogSafelyAsync(new PatientContinuousSyncRunLog
                {
                    TaskId = task.Id,
                    SessionId = session.Id,
                    InterfaceId = iface.Id,
                    PatientId = session.PatientId,
                    VisitSn = session.VisitSn,
                    ServerCode = iface.ServerCode,
                    InterfaceName = iface.DisplayName,
                    Level = "Info",
                    Message = $"查询 {records.Count} 条，新推送 {newRecords.Count} 条",
                    QueryCount = records.Count,
                    PushedCount = newRecords.Count,
                    WindowFrom = queryFrom,
                    WindowTo = queryTo
                }, ct);
            }

            if (iface.ContinuousUseTimeRange)
            {
                _logger.LogInformation(
                    "患者持续同步 [{TaskCode}] [{Interface}] 患者 {PatientId}/{VisitSn} 查询 {Count} 条，新推送 {NewCount} 条，窗口 {From} ~ {To}",
                    task.Code,
                    iface.ServerCode,
                    session.PatientId,
                    session.VisitSn,
                    records.Count,
                    newRecords.Count,
                    queryFrom,
                    queryTo);
            }
            else
            {
                _logger.LogInformation(
                    "患者持续同步 [{TaskCode}] [{Interface}] 患者 {PatientId}/{VisitSn} 无时间窗口查询 {Count} 条，新推送 {NewCount} 条",
                    task.Code,
                    iface.ServerCode,
                    session.PatientId,
                    session.VisitSn,
                    records.Count,
                    newRecords.Count);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await MarkInterfaceWaitingAsync(
                session.Id,
                iface.Id,
                DateTime.Now.AddSeconds(GetRetryDelaySeconds(state.RetryCount + 1)),
                ex.Message,
                true,
                ct);
            metrics.AddFailure(queriedCount);
            await AddRunLogSafelyAsync(new PatientContinuousSyncRunLog
            {
                TaskId = task.Id,
                SessionId = session.Id,
                InterfaceId = iface.Id,
                PatientId = session.PatientId,
                VisitSn = session.VisitSn,
                ServerCode = iface.ServerCode,
                InterfaceName = iface.DisplayName,
                Level = "Error",
                Message = ex.Message,
                QueryCount = queriedCount,
                FailedCount = 1,
                WindowFrom = queryFrom,
                WindowTo = queryTo
            }, ct);
            _logger.LogError(
                ex,
                "患者持续同步 [{TaskCode}] [{Interface}] 患者 {PatientId}/{VisitSn} 失败",
                task.Code,
                iface.ServerCode,
                session.PatientId,
                session.VisitSn);
        }
    }

    private async Task EnsureInterfaceStatesAsync(
        SyncDbContext db,
        SyncTask task,
        PatientContinuousSyncSession session,
        CancellationToken ct)
    {
        var interfaceIds = task.Interfaces.Where(IsEnabledContinuousApi).Select(item => item.Id).ToList();
        var existingIds = await db.PatientContinuousSyncInterfaceStates
            .Where(item => item.SessionId == session.Id && interfaceIds.Contains(item.InterfaceId))
            .Select(item => item.InterfaceId)
            .ToListAsync(ct);
        foreach (var interfaceId in interfaceIds.Except(existingIds))
        {
            db.PatientContinuousSyncInterfaceStates.Add(new PatientContinuousSyncInterfaceState
            {
                SessionId = session.Id,
                InterfaceId = interfaceId,
                NextRunAt = DateTime.Now
            });
        }
    }

    private async Task RefreshSessionStateAsync(
        SyncTask task,
        long sessionId,
        List<int> interfaceIds,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await db.PatientContinuousSyncSessions.FirstOrDefaultAsync(item => item.Id == sessionId, ct);
        if (session == null)
            return;
        var states = await db.PatientContinuousSyncInterfaceStates
            .Where(item => item.SessionId == sessionId && interfaceIds.Contains(item.InterfaceId))
            .ToListAsync(ct);
        if (states.Count == 0)
            return;

        var now = DateTime.Now;
        if (session.Status == PatientContinuousSyncStatuses.Closing &&
            states.All(item => item.Status == PatientContinuousInterfaceStatuses.Completed))
        {
            session.Status = PatientContinuousSyncStatuses.Completed;
            session.NextRunAt = DateTime.MaxValue;
            session.LastError = null;
        }
        else
        {
            session.NextRunAt = states
                .Where(item => item.Status != PatientContinuousInterfaceStatuses.Completed)
                .Select(item => item.NextRunAt)
                .DefaultIfEmpty(now.AddSeconds(Math.Max(60, task.PatientContinuousSyncIntervalSeconds)))
                .Min();
            session.LastError = states.Select(item => item.LastError).FirstOrDefault(error => !string.IsNullOrWhiteSpace(error));
        }

        session.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private async Task MarkInterfaceWaitingAsync(
        long sessionId,
        int interfaceId,
        DateTime nextRunAt,
        string error,
        bool incrementRetry,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var state = await db.PatientContinuousSyncInterfaceStates.FirstAsync(item =>
            item.SessionId == sessionId && item.InterfaceId == interfaceId, ct);
        state.Status = PatientContinuousInterfaceStatuses.Waiting;
        state.NextRunAt = nextRunAt;
        state.LastError = error;
        if (incrementRetry)
            state.RetryCount++;
        await db.SaveChangesAsync(ct);
    }

    private async Task UpdateSessionErrorAsync(
        long sessionId,
        string error,
        SyncTask task,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await db.PatientContinuousSyncSessions.FindAsync([sessionId], ct);
        if (session == null)
            return;
        session.LastError = error;
        session.NextRunAt = DateTime.Now.AddSeconds(Math.Max(60, task.PatientContinuousSyncIntervalSeconds));
        session.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);
    }

    private static bool IsEnabledContinuousApi(SyncTaskInterface iface)
        => iface.Enabled && iface.PatientContinuousSyncEnabled &&
           string.Equals(iface.SourceType, IngestionService.SourceTypeApi, StringComparison.OrdinalIgnoreCase) &&
           string.IsNullOrWhiteSpace(iface.ParentInterfaceKey);

    private static int GetRetryDelaySeconds(int retryCount) => retryCount switch
    {
        <= 1 => 300,
        2 => 900,
        _ => 1800
    };

    private async Task AddRunLogSafelyAsync(PatientContinuousSyncRunLog log, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.PatientContinuousSyncRunLogs.Add(log);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "患者持续同步运行日志写入失败，任务 {TaskId}", log.TaskId);
        }
    }

    private sealed class ExecutionMetrics
    {
        private int _queryCount;
        private int _pushedCount;
        private int _failedCount;

        public int QueryCount => Volatile.Read(ref _queryCount);
        public int PushedCount => Volatile.Read(ref _pushedCount);
        public int FailedCount => Volatile.Read(ref _failedCount);

        public void AddSuccess(int queryCount, int pushedCount)
        {
            Interlocked.Add(ref _queryCount, queryCount);
            Interlocked.Add(ref _pushedCount, pushedCount);
        }

        public void AddFailure(int queryCount = 0)
        {
            Interlocked.Add(ref _queryCount, queryCount);
            Interlocked.Increment(ref _failedCount);
        }
    }

    private static Dictionary<string, object> DeserializeRecord(string json)
    {
        try
        {
            var record = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            return record == null
                ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object>(record, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string BuildRecordKey(
        SyncTaskInterface iface,
        IReadOnlyDictionary<string, object> record)
    {
        var keyFields = iface.ContinuousRecordKeyArray;
        if (keyFields.Length == 0)
        {
            if (!iface.ContinuousUseRowHash)
                throw new InvalidOperationException($"接口 [{iface.DisplayName}] 未配置持续同步记录唯一键");
            return $"SHA256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalizeRecord(record))))}";
        }

        return string.Join("|", keyFields.Select(field =>
        {
            var pair = record.FirstOrDefault(item =>
                string.Equals(item.Key, field, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(pair.Key) || pair.Value == null || pair.Value is DBNull)
                throw new InvalidOperationException($"接口 [{iface.DisplayName}] 返回记录缺少唯一键字段 [{field}]");
            var value = CanonicalizeValue(pair.Value);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"接口 [{iface.DisplayName}] 返回记录的唯一键字段 [{field}] 为空");
            return $"{field}={EscapeKeyPart(value)}";
        }));
    }

    private static string CanonicalizeRecord(IReadOnlyDictionary<string, object> record)
        => string.Join("\n", record
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key}={CanonicalizeValue(item.Value)}"));

    private static string CanonicalizeValue(object? value) => value switch
    {
        null => "null",
        JsonElement element => CanonicalizeElement(element),
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? ""
    };

    private static string CanonicalizeElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{JsonSerializer.Serialize(property.Name)}:{CanonicalizeElement(property.Value)}")) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(CanonicalizeElement)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
        JsonValueKind.Number when element.TryGetDecimal(out var number) => number.ToString("G29", CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => "null",
        _ => element.GetRawText()
    };

    private static string EscapeKeyPart(string value)
        => value.Replace("\\", "\\\\").Replace("|", "\\|").Replace("=", "\\=");
}
