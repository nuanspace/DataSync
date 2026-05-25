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
    public const int MaxAutoRetryCount = 3;

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
    private readonly ILogger<PendingSyncService> _logger;

    public PendingSyncService(
        IDbContextFactory<SyncDbContext> dbFactory,
        ILogger<PendingSyncService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
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

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tasks = await db.SyncTasks
            .Where(t => t.Enabled && t.TriggerServerCode == source.ServerCode)
            .Include(t => t.Interfaces.Where(i => i.Enabled))
            .ToListAsync(ct);

        if (tasks.Count == 0)
            return [];

        var now = DateTime.Now;
        var sourceRecords = records
            .Select(record => new SourceRecordSnapshot
            {
                Record = record,
                SourceRecordKey = BuildSourceRecordKey(source.PrimaryKeyArray, record),
                SnapshotJson = JsonSerializer.Serialize(record)
            })
            .ToList();

        var notifiedTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks)
        {
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
                        SourceServerCode = source.ServerCode,
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
                    BackfillObjectIdentity(item, source.ServerCode, candidate);
                    continue;
                }

                item.ObjectKey = candidate.ObjectKey;
                item.HisPatId = candidate.HisPatId;
                item.PatVisitSn = candidate.PatVisitSn;
                item.PatName = candidate.PatName;
                if (!item.TriggerPushDone)
                {
                    item.TriggerRecordJson = candidate.SnapshotJson;
                    item.TriggerPushError = null;
                }
                item.SourceServerCode = source.ServerCode;
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
                p.RetryCount < MaxAutoRetryCount && (
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

    private static bool HasReachedRetryLimit(PendingSyncItem item)
        => item.RetryCount >= MaxAutoRetryCount;

    private static string BuildRetryLimitMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return $"已达到最大自动重试次数 {MaxAutoRetryCount}，请手动重新推送";

        return message.Contains("已达到最大自动重试次数", StringComparison.Ordinal)
            ? message
            : $"{message}；已达到最大自动重试次数 {MaxAutoRetryCount}，请手动重新推送";
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
