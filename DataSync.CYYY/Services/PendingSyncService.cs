using System.Text.Json;
using DataSync.CYYY.Data;
using DataSync.CYYY.Models;
using Microsoft.EntityFrameworkCore;

namespace DataSync.CYYY.Services;

/// <summary>
/// 待同步对象登记与状态管理
/// </summary>
public class PendingSyncService
{
    private const int DefaultMaxAutoRetryCount = 3;
    private static readonly SemaphoreSlim EnqueueLock = new(1, 1);

    private sealed class SourceRecordSnapshot
    {
        public required Dictionary<string, object> Record { get; init; }
        public required string SourceRecordKey { get; init; }
        public required string SnapshotJson { get; init; }
    }

    private sealed class PendingObjectCandidate
    {
        public required string ObjectKey { get; init; }
        public required string SourceRecordKey { get; init; }
        public required string HisPatId { get; init; }
        public required string PatVisitSn { get; init; }
        public required string PatName { get; init; }
        public required string SnapshotJson { get; init; }
    }

    private readonly IDbContextFactory<SyncDbContext> _dbFactory;
    private readonly LocalQueryService _localQueryService;
    private readonly ILogger<PendingSyncService> _logger;
    private readonly int _maxAutoRetryCount;

    public PendingSyncService(
        IDbContextFactory<SyncDbContext> dbFactory,
        LocalQueryService localQueryService,
        IConfiguration configuration,
        ILogger<PendingSyncService> logger)
    {
        _dbFactory = dbFactory;
        _localQueryService = localQueryService;
        _logger = logger;
        _maxAutoRetryCount = Math.Max(1,
            configuration.GetValue("Sync:PendingSyncMaxAutoRetryCount", DefaultMaxAutoRetryCount));
    }

    /// <summary>
    /// 采集成功后为关联任务登记待同步对象
    /// </summary>
    public async Task<List<string>> EnqueueForIngestedRecordsAsync(
        IngestionSource source,
        IReadOnlyList<Dictionary<string, object>> records,
        CancellationToken ct)
    {
        if (records.Count == 0)
            return [];

        await EnqueueLock.WaitAsync(ct);
        try
        {
            return await EnqueueForIngestedRecordsCoreAsync(source, records, ct);
        }
        finally
        {
            EnqueueLock.Release();
        }
    }

    private async Task<List<string>> EnqueueForIngestedRecordsCoreAsync(
        IngestionSource source,
        IReadOnlyList<Dictionary<string, object>> records,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var enabledTasks = await db.SyncTasks
            .Where(t => t.Enabled)
            .ToListAsync(ct);
        var tasks = enabledTasks
            .Where(task => IsTriggeredBySource(task, source.ServerCode))
            .ToList();

        if (tasks.Count == 0)
            return [];

        var now = DateTime.Now;
        var triggerServerCodes = tasks
            .Select(task => task.TriggerServerCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var triggerSources = await db.IngestionSources
            .Where(item => triggerServerCodes.Contains(item.ServerCode))
            .ToDictionaryAsync(item => item.ServerCode, StringComparer.OrdinalIgnoreCase, ct);

        var notifiedTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks)
        {
            var isPrimaryTrigger = string.Equals(
                task.TriggerServerCode,
                source.ServerCode,
                StringComparison.OrdinalIgnoreCase);
            if (!triggerSources.TryGetValue(task.TriggerServerCode, out var triggerSource))
            {
                _logger.LogWarning(
                    "任务 [{TaskCode}] 的患者来源采集源 [{ServerCode}] 不存在，无法登记待同步对象",
                    task.Code, task.TriggerServerCode);
                continue;
            }

            IReadOnlyList<Dictionary<string, object>> triggerRecords = records;
            if (!isPrimaryTrigger)
            {
                triggerRecords = await ResolveTriggerRecordsAsync(task, source, records, ct);
                if (triggerRecords.Count == 0)
                    continue;
            }

            var sourceRecords = BuildSourceRecordSnapshots(triggerSource, triggerRecords);
            var candidates = BuildPendingCandidates(task, sourceRecords);
            if (candidates.Count == 0)
                continue;

            var sourceRecordKeys = candidates
                .Select(candidate => candidate.SourceRecordKey)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var existingItems = await db.PendingSyncItems
                .Where(item => item.TaskCode == task.Code && sourceRecordKeys.Contains(item.SourceRecordKey))
                .ToListAsync(ct);

            var existingBySourceRecordKey = existingItems
                .GroupBy(item => item.SourceRecordKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                if (!existingBySourceRecordKey.TryGetValue(candidate.SourceRecordKey, out var item))
                {
                    item = new PendingSyncItem
                    {
                        TaskCode = task.Code,
                        SourceServerCode = triggerSource.ServerCode,
                        SourceRecordKey = candidate.SourceRecordKey,
                        ObjectKey = candidate.ObjectKey,
                        HisPatId = candidate.HisPatId,
                        PatVisitSn = candidate.PatVisitSn,
                        PatName = candidate.PatName,
                        TriggerRecordJson = candidate.SnapshotJson,
                        TriggerPushDone = false,
                        Status = PendingSyncStatuses.Pending,
                        CreatedAt = now,
                        UpdatedAt = now
                    };
                    db.PendingSyncItems.Add(item);
                    existingBySourceRecordKey[candidate.SourceRecordKey] = item;
                    notifiedTasks.Add(task.Code);
                    continue;
                }

                if (item.Status == PendingSyncStatuses.Success)
                {
                    BackfillObjectIdentity(item, triggerSource.ServerCode, candidate);
                    continue;
                }

                if (!isPrimaryTrigger && IsWaitingForConditions(item))
                    item.RetryCount = 0;

                item.ObjectKey = candidate.ObjectKey;
                item.HisPatId = candidate.HisPatId;
                item.PatVisitSn = candidate.PatVisitSn;
                item.PatName = candidate.PatName;
                if (!item.TriggerPushDone)
                {
                    item.TriggerRecordJson = candidate.SnapshotJson;
                    item.TriggerPushError = null;
                }
                item.SourceServerCode = triggerSource.ServerCode;
                item.SourceRecordKey = candidate.SourceRecordKey;
                if (!HasReachedRetryLimit(item))
                {
                    item.Status = PendingSyncStatuses.Pending;
                    item.LastError = null;
                    item.NextRetryTime = null;
                }
                item.UpdatedAt = now;
                existingBySourceRecordKey[candidate.SourceRecordKey] = item;
                notifiedTasks.Add(task.Code);
            }
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);

        if (notifiedTasks.Count > 0)
            _logger.LogInformation(
                "采集源 [{ServerCode}] 已登记 {TaskCount} 个待同步任务唤醒信号",
                source.ServerCode, notifiedTasks.Count);

        return [.. notifiedTasks];
    }

    private bool IsTriggeredBySource(SyncTask task, string serverCode)
    {
        if (string.Equals(task.TriggerServerCode, serverCode, StringComparison.OrdinalIgnoreCase))
            return true;

        return task.AdditionalTriggerServerCodeArray.Contains(
            serverCode,
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<Dictionary<string, object>>> ResolveTriggerRecordsAsync(
        SyncTask task,
        IngestionSource source,
        IReadOnlyList<Dictionary<string, object>> records,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task.VisitSnField))
        {
            _logger.LogWarning(
                "任务 [{TaskCode}] 未配置住院次字段，采集源 [{ServerCode}] 无法作为第二触发源",
                task.Code, source.ServerCode);
            return [];
        }

        var visitSnValues = records
            .Select(record => TryGetRecordValue(record, task.VisitSnField, out var value) ? value : "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (visitSnValues.Count == 0)
        {
            _logger.LogWarning(
                "采集源 [{ServerCode}] 返回记录缺少任务 [{TaskCode}] 的住院次字段 [{VisitSnField}]，无法触发同步",
                source.ServerCode, task.Code, task.VisitSnField);
            return [];
        }

        var triggerRecords = await _localQueryService.QueryCandidatesAsync(
            task,
            ct,
            scopeField: task.VisitSnField,
            scopeValues: visitSnValues,
            excludeSyncedOverride: true);

        if (triggerRecords.Count > 0)
        {
            _logger.LogInformation(
                "采集源 [{ServerCode}] 命中任务 [{TaskCode}] {Count} 个待同步对象",
                source.ServerCode, task.Code, triggerRecords.Count);
        }

        return triggerRecords;
    }

    private static List<SourceRecordSnapshot> BuildSourceRecordSnapshots(
        IngestionSource source,
        IReadOnlyList<Dictionary<string, object>> records)
        => records.Select(record => new SourceRecordSnapshot
        {
            Record = record,
            SourceRecordKey = BuildSourceRecordKey(source.PrimaryKeyArray, record),
            SnapshotJson = JsonSerializer.Serialize(record)
        }).ToList();

    /// <summary>
    /// 按任务领取到期的待同步对象
    /// </summary>
    public async Task<List<PendingSyncItem>> LeaseDueItemsAsync(
        string taskCode,
        int batchSize,
        TimeSpan staleAfter,
        CancellationToken ct)
    {
        var now = DateTime.Now;
        var staleTime = now - staleAfter;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var items = await db.PendingSyncItems
            .Where(p => p.TaskCode == taskCode &&
                p.RetryCount < _maxAutoRetryCount && (
                    p.Status == PendingSyncStatuses.Pending ||
                    (p.Status == PendingSyncStatuses.Waiting && (!p.NextRetryTime.HasValue || p.NextRetryTime <= now)) ||
                    (p.Status == PendingSyncStatuses.Failed && (!p.NextRetryTime.HasValue || p.NextRetryTime <= now)) ||
                    (p.Status == PendingSyncStatuses.Running && p.LastStartedAt.HasValue && p.LastStartedAt <= staleTime)))
            .OrderBy(p => p.Status == PendingSyncStatuses.Pending ? 0 :
                p.Status == PendingSyncStatuses.Failed ? 1 :
                p.Status == PendingSyncStatuses.Running ? 2 : 3)
            .ThenBy(p => p.NextRetryTime ?? p.CreatedAt)
            .ThenBy(p => p.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        if (items.Count == 0)
            return [];

        foreach (var item in items)
        {
            item.Status = PendingSyncStatuses.Running;
            item.LastStartedAt = now;
            item.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return items;
    }

    public async Task MarkSuccessAsync(long id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.PendingSyncItems.FindAsync([id], ct);
        if (item == null)
            return;

        item.Status = PendingSyncStatuses.Success;
        item.LastError = null;
        item.NextRetryTime = null;
        item.LastCompletedAt = DateTime.Now;
        item.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveCompletedInterfacesAsync(
        long id,
        IEnumerable<string> interfaceKeys,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.PendingSyncItems.FindAsync([id], ct);
        if (item == null)
            return;

        var keys = item.CompletedInterfaceKeySet;
        keys.UnionWith(interfaceKeys.Where(key => !string.IsNullOrWhiteSpace(key)));
        item.CompletedInterfaceKeys = JsonSerializer.Serialize(keys.OrderBy(key => key));
        item.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 仅因接口访问时间窗延期，不消耗自动重试次数。
    /// </summary>
    public async Task MarkDeferredAsync(long id, DateTime nextRunTime, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.PendingSyncItems.FindAsync([id], ct);
        if (item == null)
            return;

        var now = DateTime.Now;
        item.Status = PendingSyncStatuses.Waiting;
        item.LastError = $"等待接口闲时窗口，下次执行时间 {nextRunTime:yyyy-MM-dd HH:mm}";
        item.NextRetryTime = nextRunTime;
        item.LastCompletedAt = now;
        item.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> RetryManuallyAsync(long id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.PendingSyncItems.FindAsync([id], ct);
        if (item == null)
            return null;

        item.Status = PendingSyncStatuses.Pending;
        item.RetryCount = 0;
        item.LastError = null;
        item.NextRetryTime = null;
        item.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);
        return item.TaskCode;
    }

    public async Task MarkTriggerPushSuccessAsync(long id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.PendingSyncItems.FindAsync([id], ct);
        if (item == null)
            return;

        item.TriggerPushDone = true;
        item.TriggerPushDoneAt = DateTime.Now;
        item.TriggerPushError = null;
        item.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkWaitingAsync(long id, string reason, TimeSpan retryDelay, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.PendingSyncItems.FindAsync([id], ct);
        if (item == null)
            return;

        var now = DateTime.Now;
        item.RetryCount++;
        item.LastCompletedAt = now;
        item.UpdatedAt = now;

        if (HasReachedRetryLimit(item))
        {
            item.Status = PendingSyncStatuses.Failed;
            item.LastError = BuildRetryLimitMessage(reason);
            item.NextRetryTime = null;
        }
        else
        {
            item.Status = PendingSyncStatuses.Waiting;
            item.LastError = reason;
            item.NextRetryTime = now.Add(retryDelay);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task MarkSkippedAsync(long id, string reason, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.PendingSyncItems.FindAsync([id], ct);
        if (item == null)
            return;

        item.Status = PendingSyncStatuses.Skipped;
        item.LastError = reason;
        item.NextRetryTime = null;
        item.LastCompletedAt = DateTime.Now;
        item.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkFailedAsync(long id, string errorMessage, TimeSpan retryDelay, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.PendingSyncItems.FindAsync([id], ct);
        if (item == null)
            return;

        var now = DateTime.Now;
        item.Status = PendingSyncStatuses.Failed;
        item.RetryCount++;
        item.LastError = HasReachedRetryLimit(item)
            ? BuildRetryLimitMessage(errorMessage)
            : errorMessage;
        item.NextRetryTime = HasReachedRetryLimit(item) ? null : now.Add(retryDelay);
        item.LastCompletedAt = now;
        item.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkTriggerPushFailedAsync(long id, string errorMessage, TimeSpan retryDelay, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.PendingSyncItems.FindAsync([id], ct);
        if (item == null)
            return;

        var now = DateTime.Now;
        item.TriggerPushDone = false;
        item.TriggerPushDoneAt = null;
        item.TriggerPushError = errorMessage;
        item.Status = PendingSyncStatuses.Failed;
        item.RetryCount++;
        item.LastError = HasReachedRetryLimit(item)
            ? BuildRetryLimitMessage(errorMessage)
            : errorMessage;
        item.NextRetryTime = HasReachedRetryLimit(item) ? null : now.Add(retryDelay);
        item.LastCompletedAt = now;
        item.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public static string BuildSourceRecordKey(string[] primaryKeys, IReadOnlyDictionary<string, object> record)
    {
        if (primaryKeys.Length == 0)
            throw new InvalidOperationException("采集源未配置主键，无法生成待同步对象标识");

        return string.Join("|", primaryKeys.Select(key =>
        {
            if (!record.TryGetValue(key, out var value))
                throw new InvalidOperationException($"采集记录缺少主键字段 [{key}]，无法生成待同步对象标识");

            return $"{key}={EscapeKeyPart(ToText(value))}";
        }));
    }

    public static string BuildObjectKey(string hisPatId, string patVisitSn)
    {
        if (string.IsNullOrWhiteSpace(hisPatId))
            throw new InvalidOperationException("同步对象缺少患者ID，无法生成对象标识");

        var normalizedHisPatId = EscapeKeyPart(hisPatId);
        if (string.IsNullOrWhiteSpace(patVisitSn))
            return $"PAT:{normalizedHisPatId}";

        return $"PAT:{normalizedHisPatId}|VISIT:{EscapeKeyPart(patVisitSn)}";
    }

    public static Dictionary<string, object>? DeserializeTriggerRecord(string json)
        => JsonSerializer.Deserialize<Dictionary<string, object>>(json);

    private static string EscapeKeyPart(string value)
        => value.Replace("\\", "\\\\").Replace("|", "\\|").Replace("=", "\\=");

    private List<PendingObjectCandidate> BuildPendingCandidates(
        SyncTask task,
        IReadOnlyList<SourceRecordSnapshot> sourceRecords)
    {
        var candidates = new Dictionary<string, PendingObjectCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceRecord in sourceRecords)
        {
            if (!TryBuildPendingCandidate(task, sourceRecord, out var candidate))
                continue;

            if (candidates.TryGetValue(candidate.SourceRecordKey, out var existingCandidate))
            {
                if (string.IsNullOrWhiteSpace(existingCandidate.PatName) &&
                    !string.IsNullOrWhiteSpace(candidate.PatName))
                {
                    candidates[candidate.SourceRecordKey] = candidate;
                }

                continue;
            }

            candidates[candidate.SourceRecordKey] = candidate;
        }

        return [.. candidates.Values];
    }

    private bool TryBuildPendingCandidate(
        SyncTask task,
        SourceRecordSnapshot sourceRecord,
        out PendingObjectCandidate candidate)
    {
        candidate = null!;

        if (!TryGetRequiredRecordValue(sourceRecord.Record, task.PatientIdField, out var hisPatId))
        {
            _logger.LogWarning(
                "任务 [{TaskCode}] 触发记录缺少患者字段 [{PatientIdField}]，已跳过待同步登记",
                task.Code, task.PatientIdField);
            return false;
        }

        var patVisitSn = "";
        if (!string.IsNullOrWhiteSpace(task.VisitSnField) &&
            !TryGetRequiredRecordValue(sourceRecord.Record, task.VisitSnField, out patVisitSn))
        {
            _logger.LogWarning(
                "任务 [{TaskCode}] 触发记录缺少就诊号字段 [{VisitSnField}]，已跳过待同步登记",
                task.Code, task.VisitSnField);
            return false;
        }

        candidate = new PendingObjectCandidate
        {
            ObjectKey = BuildObjectKey(hisPatId, patVisitSn),
            SourceRecordKey = sourceRecord.SourceRecordKey,
            HisPatId = hisPatId,
            PatVisitSn = patVisitSn,
            PatName = GetOptionalRecordValue(sourceRecord.Record, "PAT_NAME"),
            SnapshotJson = sourceRecord.SnapshotJson
        };
        return true;
    }

    private static void BackfillObjectIdentity(
        PendingSyncItem item,
        string sourceServerCode,
        PendingObjectCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(item.ObjectKey))
            item.ObjectKey = candidate.ObjectKey;

        if (string.IsNullOrWhiteSpace(item.HisPatId))
            item.HisPatId = candidate.HisPatId;

        if (string.IsNullOrWhiteSpace(item.PatVisitSn))
            item.PatVisitSn = candidate.PatVisitSn;

        if (string.IsNullOrWhiteSpace(item.PatName))
            item.PatName = candidate.PatName;

        if (string.IsNullOrWhiteSpace(item.SourceServerCode))
            item.SourceServerCode = sourceServerCode;
    }

    private bool HasReachedRetryLimit(PendingSyncItem item)
        => item.RetryCount >= _maxAutoRetryCount;

    private static bool IsWaitingForConditions(PendingSyncItem item)
        => item.Status == PendingSyncStatuses.Waiting ||
           item.LastError?.Contains("同步条件未满足", StringComparison.Ordinal) == true;

    private string BuildRetryLimitMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return $"已达到最大自动重试次数 {_maxAutoRetryCount}，请手动重新推送";

        return message.Contains("已达到最大自动重试次数", StringComparison.Ordinal)
            ? message
            : $"{message}；已达到最大自动重试次数 {_maxAutoRetryCount}，请手动重新推送";
    }

    private static string GetOptionalRecordValue(
        IReadOnlyDictionary<string, object> record,
        string fieldName)
        => TryGetRecordValue(record, fieldName, out var value) ? value : "";

    private static bool TryGetRequiredRecordValue(
        IReadOnlyDictionary<string, object> record,
        string fieldName,
        out string value)
    {
        if (!TryGetRecordValue(record, fieldName, out value))
            return false;

        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetRecordValue(
        IReadOnlyDictionary<string, object> record,
        string fieldName,
        out string value)
    {
        value = "";

        if (record.TryGetValue(fieldName, out var matchedValue))
        {
            value = ToText(matchedValue);
            return true;
        }

        foreach (var (key, candidateValue) in record)
        {
            if (!string.Equals(key, fieldName, StringComparison.OrdinalIgnoreCase))
                continue;

            value = ToText(candidateValue);
            return true;
        }

        return false;
    }

    private static string ToText(object? value)
    {
        if (value == null)
            return "";

        return value switch
        {
            JsonElement element => element.ToString(),
            _ => value.ToString() ?? ""
        };
    }
}
