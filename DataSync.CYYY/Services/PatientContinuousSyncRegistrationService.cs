using System.Globalization;
using System.Text.Json;
using DataSync.CYYY.Data;
using DataSync.CYYY.Models;
using Microsoft.EntityFrameworkCore;

namespace DataSync.CYYY.Services;

/// <summary>
/// 根据入院、出院采集记录幂等创建患者持续同步档案。
/// </summary>
public class PatientContinuousSyncRegistrationService
{
    private static readonly SemaphoreSlim RegistrationLock = new(1, 1);

    private readonly IDbContextFactory<SyncDbContext> _dbFactory;
    private readonly LocalQueryService _localQueryService;
    private readonly ILogger<PatientContinuousSyncRegistrationService> _logger;

    public PatientContinuousSyncRegistrationService(
        IDbContextFactory<SyncDbContext> dbFactory,
        LocalQueryService localQueryService,
        ILogger<PatientContinuousSyncRegistrationService> logger)
    {
        _dbFactory = dbFactory;
        _localQueryService = localQueryService;
        _logger = logger;
    }

    public async Task EnqueueForIngestedRecordsAsync(
        IngestionSource source,
        IReadOnlyList<Dictionary<string, object>> records,
        CancellationToken ct)
    {
        if (records.Count == 0)
            return;

        await RegistrationLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var tasks = await db.SyncTasks
                .Include(task => task.Interfaces)
                .Where(task => task.Enabled && task.PatientContinuousSyncEnabled &&
                    (task.AdmissionSourceServerCode == source.ServerCode ||
                     task.DischargeSourceServerCode == source.ServerCode))
                .ToListAsync(ct);

            foreach (var task in tasks)
                await UpsertTaskRecordsAsync(db, task, source.ServerCode, records, ct);

            if (db.ChangeTracker.HasChanges())
                await db.SaveChangesAsync(ct);
        }
        finally
        {
            RegistrationLock.Release();
        }
    }

    public async Task<int> CountBootstrapCandidatesAsync(int taskId, CancellationToken ct)
        => (await LoadBootstrapCandidatesAsync(taskId, ct)).Records.Count;

    public async Task<int> BootstrapAsync(int taskId, CancellationToken ct)
    {
        var bootstrap = await LoadBootstrapCandidatesAsync(taskId, ct);
        if (bootstrap.Records.Count == 0)
            return 0;

        await RegistrationLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var task = await db.SyncTasks
                .Include(item => item.Interfaces)
                .FirstAsync(item => item.Id == taskId, ct);
            await UpsertTaskRecordsAsync(
                db,
                task,
                task.AdmissionSourceServerCode!,
                bootstrap.Records,
                ct);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            RegistrationLock.Release();
        }

        return bootstrap.Records.Count;
    }

    private async Task<(SyncTask Task, List<Dictionary<string, object>> Records)> LoadBootstrapCandidatesAsync(
        int taskId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var task = await db.SyncTasks.AsNoTracking().FirstAsync(item => item.Id == taskId, ct);
        ValidateTask(task);

        var hasFilters = !string.IsNullOrWhiteSpace(task.TriggerConditions);
        var records = hasFilters
            ? await _localQueryService.QueryCandidatesAsync(
                task,
                ct,
                excludeSyncedOverride: false)
            : await _localQueryService.QueryAllRecordsAsync(task.AdmissionSourceServerCode!, ct);
        var filteredVisits = records
            .Select(record => new
            {
                Record = record,
                PatientId = ReadText(record, task.PatientIdField),
                VisitSn = ReadText(record, task.VisitSnField!)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.PatientId) && !string.IsNullOrWhiteSpace(item.VisitSn))
            .GroupBy(item => BuildPatientVisitKey(item.PatientId, item.VisitSn), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var existingVisits = await db.PatientContinuousSyncSessions
            .AsNoTracking()
            .Where(item => item.TaskId == taskId && item.Status != PatientContinuousSyncStatuses.Completed)
            .Select(item => new { item.PatientId, item.VisitSn })
            .ToListAsync(ct);
        var existingSet = existingVisits
            .Select(item => BuildPatientVisitKey(item.PatientId, item.VisitSn))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<Dictionary<string, object>>();

        foreach (var item in filteredVisits)
        {
            if (existingSet.Contains(BuildPatientVisitKey(item.PatientId, item.VisitSn)))
                continue;

            var admissionRecord = !hasFilters || string.Equals(
                task.TriggerServerCode,
                task.AdmissionSourceServerCode,
                StringComparison.OrdinalIgnoreCase)
                ? item.Record
                : await TryQueryLatestRecordAsync(
                    task.AdmissionSourceServerCode!, task, item.PatientId, item.VisitSn, ct);
            if (admissionRecord == null ||
                !ReadTime(admissionRecord, task.AdmissionTimeField!).HasValue)
                continue;

            var dischargeRecord = await TryQueryLatestRecordAsync(
                task.DischargeSourceServerCode!, task, item.PatientId, item.VisitSn, ct);
            var discharge = ReadText(dischargeRecord, task.DischargeTimeField!);
            if (string.IsNullOrWhiteSpace(discharge))
                candidates.Add(admissionRecord);
        }

        return (task, candidates);
    }

    private async Task UpsertTaskRecordsAsync(
        SyncDbContext db,
        SyncTask task,
        string sourceServerCode,
        IReadOnlyList<Dictionary<string, object>> records,
        CancellationToken ct)
    {
        ValidateTask(task);
        var hasFilters = !string.IsNullOrWhiteSpace(task.TriggerConditions);
        HashSet<string> matchingKeys;
        try
        {
            matchingKeys = hasFilters
                ? await LoadMatchingPatientVisitKeysAsync(task, records, ct)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            matchingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _logger.LogError(ex, "患者持续同步任务 [{TaskCode}] 应用过滤条件失败，本批不创建新档案", task.Code);
        }

        foreach (var incoming in records)
        {
            var visitSn = ReadText(incoming, task.VisitSnField!);
            if (string.IsNullOrWhiteSpace(visitSn))
            {
                _logger.LogWarning(
                    "患者持续同步任务 [{TaskCode}] 的采集记录缺少就诊号字段 [{VisitField}]",
                    task.Code,
                    task.VisitSnField);
                continue;
            }

            var incomingPatientId = ReadText(incoming, task.PatientIdField);

            var admissionRecord = string.Equals(
                sourceServerCode,
                task.AdmissionSourceServerCode,
                StringComparison.OrdinalIgnoreCase)
                ? incoming
                : await TryQueryLatestRecordAsync(
                    task.AdmissionSourceServerCode!, task, incomingPatientId, visitSn, ct);
            var dischargeRecord = string.Equals(
                sourceServerCode,
                task.DischargeSourceServerCode,
                StringComparison.OrdinalIgnoreCase)
                ? incoming
                : await TryQueryLatestRecordAsync(
                    task.DischargeSourceServerCode!, task, incomingPatientId, visitSn, ct);

            var patientId = ReadText(admissionRecord, task.PatientIdField);
            if (string.IsNullOrWhiteSpace(patientId))
                patientId = incomingPatientId;

            var existing = await db.PatientContinuousSyncSessions.FirstOrDefaultAsync(item =>
                item.TaskId == task.Id && item.VisitSn == visitSn &&
                (string.IsNullOrWhiteSpace(patientId) || item.PatientId == patientId), ct);
            if (existing != null && string.IsNullOrWhiteSpace(patientId))
                patientId = existing.PatientId;
            if (string.IsNullOrWhiteSpace(patientId))
            {
                _logger.LogWarning(
                    "患者持续同步任务 [{TaskCode}] 就诊号 [{VisitSn}] 缺少患者ID字段 [{PatientField}]",
                    task.Code,
                    visitSn,
                    task.PatientIdField);
                continue;
            }

            if (existing == null && hasFilters &&
                !matchingKeys.Contains(BuildPatientVisitKey(patientId, visitSn)))
            {
                _logger.LogDebug(
                    "患者持续同步任务 [{TaskCode}] 患者 {PatientId}/{VisitSn} 未命中过滤条件，跳过建档",
                    task.Code,
                    patientId,
                    visitSn);
                continue;
            }

            var triggerRecord = DeserializeRecord(existing?.TriggerRecordJson);
            MergeRecord(triggerRecord, admissionRecord);
            MergeRecord(triggerRecord, incoming);
            MergeRecord(triggerRecord, dischargeRecord);
            triggerRecord[task.PatientIdField] = patientId;
            triggerRecord[task.VisitSnField!] = visitSn;

            var admissionTime = ReadTime(triggerRecord, task.AdmissionTimeField!);
            var dischargeTime = ReadTime(triggerRecord, task.DischargeTimeField!);
            if (existing?.Status == PatientContinuousSyncStatuses.Completed)
                continue;

            var now = DateTime.Now;
            var wasWaitingData = existing?.Status == PatientContinuousSyncStatuses.WaitingData;
            var dischargeChanged = dischargeTime.HasValue && existing?.DischargeTime != dischargeTime;
            if (existing == null)
            {
                existing = new PatientContinuousSyncSession
                {
                    TaskId = task.Id,
                    PatientId = patientId,
                    VisitSn = visitSn,
                    CreatedAt = now,
                    NextRunAt = now
                };
                db.PatientContinuousSyncSessions.Add(existing);
            }

            existing.PatientId = patientId;
            existing.AdmissionTime = admissionTime ?? existing.AdmissionTime;
            existing.DischargeTime = dischargeTime ?? existing.DischargeTime;
            existing.TriggerRecordJson = JsonSerializer.Serialize(triggerRecord);
            existing.Status = existing.AdmissionTime == null
                ? PatientContinuousSyncStatuses.WaitingData
                : existing.DischargeTime.HasValue
                    ? PatientContinuousSyncStatuses.Closing
                    : PatientContinuousSyncStatuses.Active;
            existing.LastError = existing.AdmissionTime == null ? "尚未获取到入院时间" : null;
            if (existing.Id == 0 || wasWaitingData || dischargeChanged)
                existing.NextRunAt = now;
            existing.UpdatedAt = now;

            await db.SaveChangesAsync(ct);
            await EnsureInterfaceStatesAsync(db, task, existing, now, ct);
            if (dischargeChanged)
            {
                var states = await db.PatientContinuousSyncInterfaceStates
                    .Where(item => item.SessionId == existing.Id &&
                        item.Status != PatientContinuousInterfaceStatuses.Completed)
                    .ToListAsync(ct);
                foreach (var state in states)
                {
                    state.Status = PatientContinuousInterfaceStatuses.Pending;
                    state.NextRunAt = now;
                }
            }
        }
    }

    private async Task<HashSet<string>> LoadMatchingPatientVisitKeysAsync(
        SyncTask task,
        IReadOnlyList<Dictionary<string, object>> records,
        CancellationToken ct)
    {
        var visitSns = records
            .Select(record => ReadText(record, task.VisitSnField!))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (visitSns.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var matches = await _localQueryService.QueryCandidatesAsync(
            task,
            ct,
            scopeField: task.VisitSnField,
            scopeValues: visitSns,
            excludeSyncedOverride: false);
        return matches
            .Select(record => new
            {
                PatientId = ReadText(record, task.PatientIdField),
                VisitSn = ReadText(record, task.VisitSnField!)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.PatientId) &&
                !string.IsNullOrWhiteSpace(item.VisitSn))
            .Select(item => BuildPatientVisitKey(item.PatientId, item.VisitSn))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task EnsureInterfaceStatesAsync(
        SyncDbContext db,
        SyncTask task,
        PatientContinuousSyncSession session,
        DateTime now,
        CancellationToken ct)
    {
        var interfaceIds = task.Interfaces
            .Where(item => item.Enabled && item.PatientContinuousSyncEnabled &&
                string.Equals(item.SourceType, IngestionService.SourceTypeApi, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(item.ParentInterfaceKey))
            .Select(item => item.Id)
            .ToList();
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
                NextRunAt = now
            });
        }
    }

    private static void ValidateTask(SyncTask task)
    {
        if (string.IsNullOrWhiteSpace(task.VisitSnField) ||
            string.IsNullOrWhiteSpace(task.AdmissionSourceServerCode) ||
            string.IsNullOrWhiteSpace(task.AdmissionTimeField) ||
            string.IsNullOrWhiteSpace(task.DischargeSourceServerCode) ||
            string.IsNullOrWhiteSpace(task.DischargeTimeField))
        {
            throw new InvalidOperationException($"患者持续同步任务 [{task.Name}] 的入院、出院或就诊号配置不完整");
        }
    }

    private async Task<Dictionary<string, object>?> TryQueryLatestRecordAsync(
        string serverCode,
        SyncTask task,
        string patientId,
        string visitSn,
        CancellationToken ct)
    {
        try
        {
            var matches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [task.VisitSnField!] = visitSn
            };
            if (!string.IsNullOrWhiteSpace(patientId))
                matches[task.PatientIdField] = patientId;
            return await _localQueryService.QueryLatestRecordAsync(serverCode, matches, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "患者持续同步尚未读取到采集源 [{ServerCode}] 的本地记录", serverCode);
            return null;
        }
    }

    private static Dictionary<string, object> DeserializeRecord(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

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

    private static void MergeRecord(
        IDictionary<string, object> target,
        IReadOnlyDictionary<string, object>? source)
    {
        if (source == null)
            return;
        foreach (var (key, value) in source)
            target[key] = value;
    }

    private static string ReadText(IReadOnlyDictionary<string, object>? record, string field)
    {
        if (record == null)
            return "";
        var pair = record.FirstOrDefault(item =>
            string.Equals(item.Key, field, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(pair.Key) || pair.Value == null || pair.Value is DBNull)
            return "";
        return pair.Value is JsonElement element ? element.ToString() : pair.Value.ToString() ?? "";
    }

    private static DateTime? ReadTime(IReadOnlyDictionary<string, object> record, string field)
    {
        var text = ReadText(record, field);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var value) ||
            DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out value))
        {
            return value;
        }

        throw new InvalidOperationException($"时间字段 [{field}] 格式无效：{text}");
    }

    private static string BuildPatientVisitKey(string patientId, string visitSn)
        => $"{patientId}\u001f{visitSn}";
}
