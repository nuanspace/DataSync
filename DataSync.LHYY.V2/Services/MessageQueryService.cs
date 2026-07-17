using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using DataSync.LHYY.V2.Services.FollowUp;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 消息查询、统计和重试服务。
/// </summary>
public class MessageQueryService
{
    private const int DefaultHotDays = 30;
    private const string MaintenanceMessage = "系统维护中，请稍后重试";

    private const string AllMessagesSql = """
        SELECT
            id,
            message_id,
            source_message_id,
            tran_code,
            integration_project_code,
            tran_name,
            app_id,
            org_id,
            esb_timestamp,
            raw_json,
            body_json,
            idempotent_key,
            mrn,
            visit_no,
            inpatient_no,
            resolved_event_time,
            matched_rule_group,
            status,
            retry_count,
            error_message,
            patient_id,
            event_id,
            processed_at,
            processing_started_at,
            created_at
        FROM lhyy.esb_messages_all
        """;

    private const string AllProcessLogsSql = """
        SELECT
            id,
            message_id,
            integration_project_code,
            step,
            is_success,
            detail,
            elapsed_ms,
            created_at
        FROM lhyy.esb_process_log_all
        """;

    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;
    private readonly IntegrationProjectService _integrationProjectService;
    private readonly ConfigService _configService;
    private readonly EsbReceiverService _receiverService;
    private readonly FollowUpCubeOperationCoordinator _operationCoordinator;

    public MessageQueryService(
        IDbContextFactory<DataSyncDbContext> contextFactory,
        IntegrationProjectService integrationProjectService,
        ConfigService configService,
        EsbReceiverService receiverService,
        FollowUpCubeOperationCoordinator operationCoordinator)
    {
        _contextFactory = contextFactory;
        _integrationProjectService = integrationProjectService;
        _configService = configService;
        _receiverService = receiverService;
        _operationCoordinator = operationCoordinator;
    }

    public async Task<int> GetHotRetentionDaysAsync()
    {
        var value = await _configService.GetGlobalConfigValueAsync("MessageHotRetentionDays");
        return int.TryParse(value, out var days) && days > 0 ? days : DefaultHotDays;
    }

    public async Task<TodaySummary> GetTodaySummaryAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var today = DateTime.Today;
        if (!await IsArchiveReadableAsync(db))
        {
            var legacyQuery = db.EsbMessages
                .WhereInProjectOrGlobal(currentProjectCode)
                .Where(m => m.CreatedAt >= today);

            return new TodaySummary
            {
                Total = await legacyQuery.CountAsync(),
                Pending = await legacyQuery.CountAsync(m => m.Status == MessageStatus.Pending),
                Processing = await legacyQuery.CountAsync(m => m.Status == MessageStatus.Processing),
                Success = await legacyQuery.CountAsync(m => m.Status == MessageStatus.Success),
                Failed = await legacyQuery.CountAsync(m => m.Status == MessageStatus.Failed),
                WaitingIdentity = await legacyQuery.CountAsync(m => m.Status == MessageStatus.WaitingIdentity),
            };
        }

        var query = db.EsbMessageListItems
            .WhereInProjectOrGlobal(currentProjectCode)
            .Where(m => m.CreatedAt >= today);
        return new TodaySummary
        {
            Total = await query.CountAsync(),
            Pending = await query.CountAsync(m => m.Status == MessageStatus.Pending),
            Processing = await query.CountAsync(m => m.Status == MessageStatus.Processing),
            Success = await query.CountAsync(m => m.Status == MessageStatus.Success),
            Failed = await query.CountAsync(m => m.Status == MessageStatus.Failed),
            WaitingIdentity = await query.CountAsync(m => m.Status == MessageStatus.WaitingIdentity),
        };
    }

    public async Task<List<TranCodeStat>> GetTodayTranCodeStatsAsync()
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var today = DateTime.Today;

        if (!await IsArchiveReadableAsync(db))
        {
            return await db.EsbMessages
                .WhereInProjectOrGlobal(currentProjectCode)
                .Where(m => m.CreatedAt >= today)
                .GroupBy(m => new { m.TranCode, m.TranName })
                .Select(g => new TranCodeStat
                {
                    TranCode = g.Key.TranCode,
                    TranName = g.Key.TranName ?? "",
                    Total = g.Count(),
                    Pending = g.Count(m => m.Status == MessageStatus.Pending),
                    Processing = g.Count(m => m.Status == MessageStatus.Processing),
                    Success = g.Count(m => m.Status == MessageStatus.Success),
                    Failed = g.Count(m => m.Status == MessageStatus.Failed),
                    WaitingIdentity = g.Count(m => m.Status == MessageStatus.WaitingIdentity),
                })
                .OrderBy(s => s.TranCode)
                .ToListAsync();
        }

        return await db.EsbMessageListItems
            .WhereInProjectOrGlobal(currentProjectCode)
            .Where(m => m.CreatedAt >= today)
            .GroupBy(m => new { m.TranCode, m.TranName })
            .Select(g => new TranCodeStat
            {
                TranCode = g.Key.TranCode,
                TranName = g.Key.TranName ?? "",
                Total = g.Count(),
                Pending = g.Count(m => m.Status == MessageStatus.Pending),
                Processing = g.Count(m => m.Status == MessageStatus.Processing),
                Success = g.Count(m => m.Status == MessageStatus.Success),
                Failed = g.Count(m => m.Status == MessageStatus.Failed),
                WaitingIdentity = g.Count(m => m.Status == MessageStatus.WaitingIdentity),
            })
            .OrderBy(s => s.TranCode)
            .ToListAsync();
    }

    public async Task<List<EsbMessageListItem>> GetRecentMessagesAsync(int count = 20)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var hotStartTime = DateTime.Today.AddDays(-await GetHotRetentionDaysAsync());

        if (!await IsArchiveReadableAsync(db))
        {
            return await db.EsbMessages
                .AsNoTracking()
                .WhereInProjectOrGlobal(currentProjectCode)
                .Where(m => m.CreatedAt >= hotStartTime)
                .OrderByDescending(m => m.CreatedAt)
                .ThenByDescending(m => m.Id)
                .Take(count)
                .Select(m => new EsbMessageListItem
                {
                    Id = m.Id,
                    MessageId = m.MessageId,
                    TranCode = m.TranCode,
                    IntegrationProjectCode = m.IntegrationProjectCode,
                    TranName = m.TranName,
                    Mrn = m.Mrn,
                    VisitNo = m.VisitNo,
                    InpatientNo = m.InpatientNo,
                    ResolvedEventTime = m.ResolvedEventTime,
                    Status = m.Status,
                    RetryCount = m.RetryCount,
                    ErrorMessage = m.ErrorMessage,
                    CreatedAt = m.CreatedAt
                })
                .ToListAsync();
        }

        return await db.EsbMessageListItems
            .AsNoTracking()
            .WhereInProjectOrGlobal(currentProjectCode)
            .Where(m => m.CreatedAt >= hotStartTime)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(count)
            .ToListAsync();
    }

    public async Task<(List<EsbMessageListItem> Items, int TotalCount)> GetMessagesPagedAsync(
        int page,
        int pageSize,
        string? tranCode = null,
        MessageStatus? status = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? mrn = null,
        string? inpatientNo = null,
        string? visitNo = null,
        DateTime? eventStartTime = null,
        DateTime? eventEndTime = null)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var effectiveStartTime = await ResolveEffectiveStartTimeAsync(startTime, endTime);

        if (!await IsArchiveReadableAsync(db))
        {
            var legacyQuery = ApplyFilters(
                db.EsbMessages.AsNoTracking().WhereInProjectOrGlobal(currentProjectCode),
                tranCode,
                status,
                effectiveStartTime,
                endTime,
                mrn,
                inpatientNo,
                visitNo,
                eventStartTime,
                eventEndTime);

            var legacyTotalCount = await legacyQuery.CountAsync();
            var legacyItems = await legacyQuery
                .OrderByDescending(m => m.CreatedAt)
                .ThenByDescending(m => m.Id)
                .Skip(page * pageSize)
                .Take(pageSize)
                .Select(m => new EsbMessageListItem
                {
                    Id = m.Id,
                    MessageId = m.MessageId,
                    TranCode = m.TranCode,
                    IntegrationProjectCode = m.IntegrationProjectCode,
                    TranName = m.TranName,
                    Mrn = m.Mrn,
                    VisitNo = m.VisitNo,
                    InpatientNo = m.InpatientNo,
                    ResolvedEventTime = m.ResolvedEventTime,
                    Status = m.Status,
                    RetryCount = m.RetryCount,
                    ErrorMessage = m.ErrorMessage,
                    CreatedAt = m.CreatedAt
                })
                .ToListAsync();
            await FillMissingQueryFieldsAsync(db, legacyItems, currentProjectCode, useArchiveView: false);

            return (legacyItems, legacyTotalCount);
        }

        var query = ApplyFilters(
            db.EsbMessageListItems.AsNoTracking().WhereInProjectOrGlobal(currentProjectCode),
            tranCode,
            status,
            effectiveStartTime,
            endTime,
            mrn,
            inpatientNo,
            visitNo,
            eventStartTime,
            eventEndTime);

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToListAsync();
        await FillMissingQueryFieldsAsync(db, items, currentProjectCode, useArchiveView: true);

        return (items, totalCount);
    }

    public async Task<EsbMessage?> GetMessageByIdAsync(long id)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        if (!await IsArchiveReadableAsync(db))
        {
            return await db.EsbMessages
                .AsNoTracking()
                .WhereInProjectOrGlobal(currentProjectCode)
                .FirstOrDefaultAsync(m => m.Id == id);
        }

        return await db.EsbMessages
            .FromSqlRaw(AllMessagesSql)
            .AsNoTracking()
            .WhereInProjectOrGlobal(currentProjectCode)
            .FirstOrDefaultAsync(m => m.Id == id);
    }

    public async Task<List<EsbProcessLog>> GetProcessLogsAsync(long messageId)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        if (!await IsArchiveReadableAsync(db))
        {
            return await db.EsbProcessLogs
                .AsNoTracking()
                .WhereInProjectOrGlobal(currentProjectCode)
                .Where(l => l.MessageId == messageId)
                .OrderBy(l => l.CreatedAt)
                .ThenBy(l => l.Id)
                .ToListAsync();
        }

        return await db.EsbProcessLogs
            .FromSqlRaw(AllProcessLogsSql)
            .AsNoTracking()
            .WhereInProjectOrGlobal(currentProjectCode)
            .Where(l => l.MessageId == messageId)
            .OrderBy(l => l.CreatedAt)
            .ThenBy(l => l.Id)
            .ToListAsync();
    }

    public async Task<bool> RetryMessageAsync(long id)
    {
        await using var operationLease = await _operationCoordinator.TryAcquireSharedAsync(CancellationToken.None);
        if (operationLease is null) throw new InvalidOperationException(MaintenanceMessage);
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var message = await db.EsbMessages
            .WhereInProjectOrGlobal(currentProjectCode)
            .FirstOrDefaultAsync(m => m.Id == id);
        if (message == null || message.Status != MessageStatus.Failed)
            return false;

        message.Status = MessageStatus.Pending;
        message.ErrorMessage = null;
        message.ProcessingStartedAt = null;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ProcessMessageNowAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var operationLease = await _operationCoordinator.TryAcquireSharedAsync(cancellationToken);
        if (operationLease is null) throw new InvalidOperationException(MaintenanceMessage);
        if (!await PrepareMessageForDirectProcessAsync(id, cancellationToken))
            return false;

        try
        {
            await _receiverService.ProcessQueuedMessageAsync(id, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            await MarkDirectProcessFailedAsync(id, ex.Message, cancellationToken);
            throw;
        }
    }

    public async Task<int> BatchRetryAsync(
        string? tranCode = null,
        MessageStatus? status = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? mrn = null,
        string? inpatientNo = null,
        string? visitNo = null,
        DateTime? eventStartTime = null,
        DateTime? eventEndTime = null)
    {
        await using var operationLease = await _operationCoordinator.TryAcquireSharedAsync(CancellationToken.None);
        if (operationLease is null) throw new InvalidOperationException(MaintenanceMessage);
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var effectiveStartTime = await ResolveEffectiveStartTimeAsync(startTime, endTime);
        var query = ApplyFilters(
                db.EsbMessages.WhereInProjectOrGlobal(currentProjectCode),
                tranCode,
                status,
                effectiveStartTime,
                endTime,
                mrn,
                inpatientNo,
                visitNo,
                eventStartTime,
                eventEndTime)
            .Where(m => m.Status == MessageStatus.Failed);

        return await query.ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, MessageStatus.Pending)
            .SetProperty(m => m.ErrorMessage, (string?)null)
            .SetProperty(m => m.ProcessingStartedAt, (DateTime?)null));
    }

    private async Task<bool> PrepareMessageForDirectProcessAsync(long id, CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var message = await db.EsbMessages
            .WhereInProjectOrGlobal(currentProjectCode)
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (message == null || !CanDirectProcess(message.Status))
            return false;

        var originalStatus = message.Status;
        await ClearMessageReceiptsAsync(db, message, cancellationToken);

        message.Status = MessageStatus.Processing;
        message.ErrorMessage = null;
        message.ProcessedAt = null;
        message.ProcessingStartedAt = DateTime.Now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task MarkDirectProcessFailedAsync(long id, string errorMessage, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var message = await db.EsbMessages.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
            if (message == null)
                return;

            message.Status = MessageStatus.Failed;
            message.ErrorMessage = $"直接处理失败: {errorMessage}";
            message.ProcessedAt = DateTime.Now;
            message.ProcessingStartedAt = null;
            message.RetryCount++;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // 保留原始异常给页面提示。
        }
    }

    private static async Task ClearMessageReceiptsAsync(DataSyncDbContext db, EsbMessage message, CancellationToken cancellationToken)
    {
        var sourceMessageId = string.IsNullOrWhiteSpace(message.SourceMessageId) ? null : message.SourceMessageId;
        var idempotentKey = string.IsNullOrWhiteSpace(message.IdempotentKey) ? null : message.IdempotentKey;
        if (sourceMessageId == null && idempotentKey == null)
            return;

        var query = db.EsbMessageReceipts
            .Where(r => r.IntegrationProjectCode == message.IntegrationProjectCode && r.TranCode == message.TranCode);

        if (sourceMessageId != null && idempotentKey != null)
            query = query.Where(r => r.SourceMessageId == sourceMessageId || r.IdempotentKey == idempotentKey);
        else if (sourceMessageId != null)
            query = query.Where(r => r.SourceMessageId == sourceMessageId);
        else
            query = query.Where(r => r.IdempotentKey == idempotentKey);

        await query.ExecuteDeleteAsync(cancellationToken);
    }

    private static bool CanDirectProcess(MessageStatus status) =>
        status != MessageStatus.Processing;

    public async Task<List<string>> GetDistinctTranCodesAsync(DateTime? startTime = null, DateTime? endTime = null)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var effectiveStartTime = await ResolveEffectiveStartTimeAsync(startTime, endTime);

        if (!await IsArchiveReadableAsync(db))
        {
            return await db.EsbMessages
                .WhereInProjectOrGlobal(currentProjectCode)
                .Where(m => m.TranCode != "")
                .Where(m => m.CreatedAt >= effectiveStartTime)
                .Where(m => !endTime.HasValue || m.CreatedAt < endTime.Value.AddMinutes(1))
                .Select(m => m.TranCode)
                .Distinct()
                .OrderBy(c => c)
                .ToListAsync();
        }

        return await db.EsbMessageListItems
            .WhereInProjectOrGlobal(currentProjectCode)
            .Where(m => m.TranCode != "")
            .Where(m => m.CreatedAt >= effectiveStartTime)
            .Where(m => !endTime.HasValue || m.CreatedAt < endTime.Value.AddMinutes(1))
            .Select(m => m.TranCode)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();
    }

    private async Task<DateTime> ResolveEffectiveStartTimeAsync(DateTime? startTime, DateTime? endTime)
    {
        if (startTime.HasValue)
            return startTime.Value;

        if (endTime.HasValue)
            return DateTime.MinValue;

        var hotDays = await GetHotRetentionDaysAsync();
        return DateTime.Today.AddDays(-hotDays);
    }

    private static IQueryable<EsbMessageListItem> ApplyFilters(
        IQueryable<EsbMessageListItem> query,
        string? tranCode = null,
        MessageStatus? status = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? mrn = null,
        string? inpatientNo = null,
        string? visitNo = null,
        DateTime? eventStartTime = null,
        DateTime? eventEndTime = null)
    {
        if (!string.IsNullOrWhiteSpace(tranCode))
            query = query.Where(m => m.TranCode == tranCode);

        if (status.HasValue)
            query = query.Where(m => m.Status == status.Value);

        if (startTime.HasValue)
            query = query.Where(m => m.CreatedAt >= startTime.Value);

        if (endTime.HasValue)
            query = query.Where(m => m.CreatedAt < endTime.Value.AddMinutes(1));

        if (!string.IsNullOrWhiteSpace(mrn))
            query = query.Where(m => m.Mrn == mrn.Trim());

        if (!string.IsNullOrWhiteSpace(inpatientNo))
            query = query.Where(m => m.InpatientNo == inpatientNo.Trim());

        if (!string.IsNullOrWhiteSpace(visitNo))
            query = query.Where(m => m.VisitNo == visitNo.Trim());

        if (eventStartTime.HasValue)
            query = query.Where(m => m.ResolvedEventTime >= eventStartTime.Value);

        if (eventEndTime.HasValue)
            query = query.Where(m => m.ResolvedEventTime < eventEndTime.Value.AddMinutes(1));

        return query;
    }

    private static IQueryable<EsbMessage> ApplyFilters(
        IQueryable<EsbMessage> query,
        string? tranCode = null,
        MessageStatus? status = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? mrn = null,
        string? inpatientNo = null,
        string? visitNo = null,
        DateTime? eventStartTime = null,
        DateTime? eventEndTime = null)
    {
        if (!string.IsNullOrWhiteSpace(tranCode))
            query = query.Where(m => m.TranCode == tranCode);

        if (status.HasValue)
            query = query.Where(m => m.Status == status.Value);

        if (startTime.HasValue)
            query = query.Where(m => m.CreatedAt >= startTime.Value);

        if (endTime.HasValue)
            query = query.Where(m => m.CreatedAt < endTime.Value.AddMinutes(1));

        if (!string.IsNullOrWhiteSpace(mrn))
            query = query.Where(m => m.Mrn == mrn.Trim());

        if (!string.IsNullOrWhiteSpace(inpatientNo))
            query = query.Where(m => m.InpatientNo == inpatientNo.Trim());

        if (!string.IsNullOrWhiteSpace(visitNo))
            query = query.Where(m => m.VisitNo == visitNo.Trim());

        if (eventStartTime.HasValue)
            query = query.Where(m => m.ResolvedEventTime >= eventStartTime.Value);

        if (eventEndTime.HasValue)
            query = query.Where(m => m.ResolvedEventTime < eventEndTime.Value.AddMinutes(1));

        return query;
    }

    private static async Task FillMissingQueryFieldsAsync(
        DataSyncDbContext db,
        List<EsbMessageListItem> items,
        string? currentProjectCode,
        bool useArchiveView)
    {
        var fallbackItems = items.Where(NeedsQueryFieldFallback).ToList();
        if (fallbackItems.Count == 0)
            return;

        var ids = fallbackItems.Select(m => m.Id).ToList();
        var sourceQuery = useArchiveView
            ? db.EsbMessages.FromSqlRaw(AllMessagesSql)
            : db.EsbMessages.AsQueryable();
        var fullMessages = await sourceQuery
            .AsNoTracking()
            .WhereInProjectOrGlobal(currentProjectCode)
            .Where(m => ids.Contains(m.Id))
            .ToListAsync();
        if (fullMessages.Count == 0)
            return;

        var tranCodes = fullMessages
            .Where(NeedsQueryFieldFallback)
            .Select(m => m.TranCode)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tranCodes.Count == 0)
            return;

        var configs = await db.EsbInterfaceConfigs
            .AsNoTracking()
            .WhereInProjectOrGlobal(currentProjectCode)
            .Where(c => tranCodes.Contains(c.TranCode))
            .ToListAsync();
        var configByCode = configs
            .GroupBy(c => c.TranCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(c => string.Equals(c.IntegrationProjectCode, currentProjectCode, StringComparison.OrdinalIgnoreCase)).First(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var message in fullMessages.Where(NeedsQueryFieldFallback))
        {
            if (!configByCode.TryGetValue(message.TranCode, out var config))
                continue;

            if (MessageJsonHelper.TryParseToken(message.RawJson, out var root, out _))
                ApplyQueryFields(message, config, root);

            if (NeedsQueryFieldFallback(message))
                ApplyBodyJsonQueryFields(message, config);
        }

        var fullMessageById = fullMessages.ToDictionary(m => m.Id);
        foreach (var item in fallbackItems)
        {
            if (!fullMessageById.TryGetValue(item.Id, out var message))
                continue;

            item.Mrn = message.Mrn;
            item.VisitNo = message.VisitNo;
            item.InpatientNo = message.InpatientNo;
            item.ResolvedEventTime = message.ResolvedEventTime;
        }
    }

    private static bool NeedsQueryFieldFallback(EsbMessageListItem message) =>
        string.IsNullOrWhiteSpace(message.Mrn) ||
        string.IsNullOrWhiteSpace(message.VisitNo) ||
        string.IsNullOrWhiteSpace(message.InpatientNo) ||
        !message.ResolvedEventTime.HasValue;

    private static bool NeedsQueryFieldFallback(EsbMessage message) =>
        string.IsNullOrWhiteSpace(message.Mrn) ||
        string.IsNullOrWhiteSpace(message.VisitNo) ||
        string.IsNullOrWhiteSpace(message.InpatientNo) ||
        !message.ResolvedEventTime.HasValue;

    private static void ApplyBodyJsonQueryFields(EsbMessage message, EsbInterfaceConfig config)
    {
        if (!MessageJsonHelper.TryParseToken(message.BodyJson ?? "", out var body, out _))
            return;

        var wrappedBody = new JObject
        {
            ["Request"] = new JObject
            {
                ["Body"] = body
            }
        };
        ApplyQueryFields(message, config, wrappedBody);

        if (NeedsQueryFieldFallback(message))
            ApplyQueryFields(message, config, body);
    }

    private static void ApplyQueryFields(EsbMessage message, EsbInterfaceConfig config, JToken root)
    {
        var mainContext = MessageJsonHelper.ResolveMainRecordContext(root, config.MainRecordArrayPath);

        if (string.IsNullOrWhiteSpace(message.Mrn))
            message.Mrn = MessageJsonHelper.ReadString(root, config.MrnSourcePath, mainContext);

        if (!message.ResolvedEventTime.HasValue)
            message.ResolvedEventTime = MessageJsonHelper.ReadDateTime(root, config.EventStartTimeSourcePath, mainContext);

        if (string.IsNullOrWhiteSpace(message.VisitNo))
            message.VisitNo = MessageJsonHelper.ReadString(root, config.VisitNoSourcePath, mainContext);

        if (string.IsNullOrWhiteSpace(message.InpatientNo))
            message.InpatientNo = MessageJsonHelper.ReadString(root, config.InpatientNoSourcePath, mainContext);
    }

    private static async Task<bool> IsArchiveReadableAsync(DataSyncDbContext db)
    {
        try
        {
            var connection = db.Database.GetDbConnection();
            return await ArchiveOptimizationCheck.IsReadableAsync(connection);
        }
        catch
        {
            return false;
        }
    }
}

public class TodaySummary
{
    public int Total { get; set; }
    public int Pending { get; set; }
    public int Processing { get; set; }
    public int Success { get; set; }
    public int Failed { get; set; }
    public int WaitingIdentity { get; set; }
}

public class TranCodeStat
{
    public string TranCode { get; set; } = "";
    public string TranName { get; set; } = "";
    public int Total { get; set; }
    public int Pending { get; set; }
    public int Processing { get; set; }
    public int Success { get; set; }
    public int Failed { get; set; }
    public int WaitingIdentity { get; set; }
}
