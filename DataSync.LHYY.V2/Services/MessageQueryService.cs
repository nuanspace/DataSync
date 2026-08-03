using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using DataSync.LHYY.V2.Services.FollowUp;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Newtonsoft.Json.Linq;
using Npgsql;
using NpgsqlTypes;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 消息查询、统计和重试服务。
/// </summary>
public class MessageQueryService
{
    private const int DefaultHotDays = 30;
    private const string MaintenanceMessage = "系统维护中，请稍后重试";
    private const long ArchiveLockKey = 2026052601;

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
    private readonly MessageProcessingNotifier? _messageProcessingNotifier;

    public MessageQueryService(
        IDbContextFactory<DataSyncDbContext> contextFactory,
        IntegrationProjectService integrationProjectService,
        ConfigService configService,
        EsbReceiverService receiverService,
        FollowUpCubeOperationCoordinator operationCoordinator)
        : this(
            contextFactory,
            integrationProjectService,
            configService,
            receiverService,
            operationCoordinator,
            null)
    {
    }

    public MessageQueryService(
        IDbContextFactory<DataSyncDbContext> contextFactory,
        IntegrationProjectService integrationProjectService,
        ConfigService configService,
        EsbReceiverService receiverService,
        FollowUpCubeOperationCoordinator operationCoordinator,
        MessageProcessingNotifier? messageProcessingNotifier)
    {
        _contextFactory = contextFactory;
        _integrationProjectService = integrationProjectService;
        _configService = configService;
        _receiverService = receiverService;
        _operationCoordinator = operationCoordinator;
        _messageProcessingNotifier = messageProcessingNotifier;
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

    public static bool CanBatchRetry(MessageStatus status) => status is
        MessageStatus.Failed or
        MessageStatus.Success or
        MessageStatus.Filtered or
        MessageStatus.Unmatched or
        MessageStatus.PartialSuccess or
        MessageStatus.WaitingIdentity;

    public async Task<BatchRetryPreview> PreviewBatchRetryAsync(
        IReadOnlyCollection<long> messageIds,
        CancellationToken cancellationToken = default)
    {
        var ids = messageIds.Distinct().ToArray();
        var preview = new BatchRetryPreview { SelectedCount = ids.Length };
        if (ids.Length == 0)
            return preview;

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var messages = await LoadRetryMessagesAsync(
            db,
            ids,
            currentProjectCode,
            await IsArchiveReadableAsync(db),
            cancellationToken);
        if (messages.GroupBy(item => item.Message.Id).Any(group => group.Count() > 1))
            throw new InvalidOperationException("热表与归档表存在重复消息，无法批量重试。");

        var messageById = messages.ToDictionary(item => item.Message.Id);

        foreach (var id in ids)
        {
            if (!messageById.TryGetValue(id, out var item))
            {
                preview.SkippedItems.Add(CreateSkippedItem(id, null, "消息不存在或不属于当前项目"));
                continue;
            }

            if (!CanBatchRetry(item.Message.Status))
            {
                preview.SkippedItems.Add(CreateSkippedItem(
                    id,
                    item.Message.MessageId,
                    $"{item.Message.Status.ToDisplayText()}状态不允许重试"));
                continue;
            }

            preview.ValidCount++;
            if (item.IsArchive)
                preview.ArchiveCount++;
            else
                preview.HotCount++;

            preview.StatusCounts[item.Message.Status] =
                preview.StatusCounts.GetValueOrDefault(item.Message.Status) + 1;
        }

        return preview;
    }

    public async Task<BatchRetryResult> BatchRetryAsync(
        IReadOnlyCollection<long> messageIds,
        CancellationToken cancellationToken = default)
    {
        await using var operationLease = await _operationCoordinator.TryAcquireSharedAsync(cancellationToken);
        if (operationLease is null)
            throw new InvalidOperationException(MaintenanceMessage);

        var ids = messageIds.Distinct().ToArray();
        var result = new BatchRetryResult();
        if (ids.Length == 0)
            return result;

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var archiveReadable = await IsArchiveReadableAsync(db);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var dbTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();
            await AcquireArchiveLockAsync(connection, dbTransaction, cancellationToken);
            var lockedMessages = await LoadLockedRetryMessagesAsync(
                connection,
                dbTransaction,
                ids,
                currentProjectCode,
                archiveReadable,
                cancellationToken);
            var messageById = lockedMessages.ToDictionary(item => item.Message.Id);
            var validMessages = new List<RetryMessageSnapshot>();

            foreach (var id in ids)
            {
                if (!messageById.TryGetValue(id, out var item))
                {
                    result.SkippedItems.Add(CreateSkippedItem(id, null, "消息不存在或不属于当前项目"));
                    continue;
                }

                if (!CanBatchRetry(item.Message.Status))
                {
                    result.SkippedItems.Add(CreateSkippedItem(
                        id,
                        item.Message.MessageId,
                        $"消息状态已变为{item.Message.Status.ToDisplayText()}，已跳过"));
                    continue;
                }

                validMessages.Add(item);
            }

            var receiptKeys = new HashSet<MessageReceiptKey>();
            foreach (var item in validMessages)
            {
                var keys = await _receiverService.ResolveReplayReceiptKeysAsync(item.Message, cancellationToken);
                receiptKeys.UnionWith(keys);
            }

            await AcquireReceiptLocksAsync(connection, dbTransaction, receiptKeys, cancellationToken);
            await ClearMessageReceiptsAsync(db, receiptKeys, cancellationToken);

            var archiveMessages = validMessages
                .Where(item => item.IsArchive)
                .ToArray();
            if (archiveMessages.Length > 0)
            {
                await RestoreArchiveMessagesAsync(
                    connection,
                    dbTransaction,
                    archiveMessages,
                    currentProjectCode,
                    cancellationToken);
            }

            var validIds = validMessages.Select(item => item.Message.Id).ToArray();
            if (validIds.Length > 0)
                await ResetMessagesForRetryAsync(connection, dbTransaction, validIds, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            result.SubmittedCount = validIds.Length;
            result.RestoredArchiveCount = archiveMessages.Length;
            if (result.SubmittedCount > 0)
                _messageProcessingNotifier?.Notify();

            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<List<RetryMessageSnapshot>> LoadRetryMessagesAsync(
        DataSyncDbContext db,
        long[] ids,
        string? currentProjectCode,
        bool archiveReadable,
        CancellationToken cancellationToken)
    {
        if (!archiveReadable)
        {
            return (await db.EsbMessages
                    .AsNoTracking()
                    .WhereInProjectOrGlobal(currentProjectCode)
                    .Where(message => ids.Contains(message.Id))
                    .ToListAsync(cancellationToken))
                .Select(message => new RetryMessageSnapshot(message, false))
                .ToList();
        }

        var messages = await db.EsbMessages
            .FromSqlRaw(AllMessagesSql)
            .AsNoTracking()
            .WhereInProjectOrGlobal(currentProjectCode)
            .Where(message => ids.Contains(message.Id))
            .ToListAsync(cancellationToken);
        var hotIds = await db.EsbMessages
            .AsNoTracking()
            .WhereInProjectOrGlobal(currentProjectCode)
            .Where(message => ids.Contains(message.Id))
            .Select(message => message.Id)
            .ToListAsync(cancellationToken);
        var hotIdSet = hotIds.ToHashSet();
        return messages
            .Select(message => new RetryMessageSnapshot(message, !hotIdSet.Contains(message.Id)))
            .ToList();
    }

    private static async Task AcquireArchiveLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@lock_key);",
            connection,
            transaction);
        command.Parameters.AddWithValue("lock_key", NpgsqlDbType.Bigint, ArchiveLockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<RetryMessageSnapshot>> LoadLockedRetryMessagesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long[] ids,
        string? currentProjectCode,
        bool archiveReadable,
        CancellationToken cancellationToken)
    {
        var messages = await LoadLockedRetryMessagesFromTableAsync(
            connection,
            transaction,
            "esb_messages",
            false,
            ids,
            currentProjectCode,
            cancellationToken);
        if (!archiveReadable)
            return messages;

        var archiveMessages = await LoadLockedRetryMessagesFromTableAsync(
            connection,
            transaction,
            "esb_messages_archive",
            true,
            ids,
            currentProjectCode,
            cancellationToken);
        var loadedIds = messages.Select(item => item.Message.Id).ToHashSet();
        if (archiveMessages.Any(item => !loadedIds.Add(item.Message.Id)))
            throw new InvalidOperationException("热表与归档表存在重复消息，无法批量重试。");

        messages.AddRange(archiveMessages);
        return messages;
    }

    private static async Task<List<RetryMessageSnapshot>> LoadLockedRetryMessagesFromTableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        bool isArchive,
        long[] ids,
        string? currentProjectCode,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT
                id,
                message_id,
                source_message_id,
                tran_code,
                integration_project_code,
                raw_json,
                idempotent_key,
                status,
                created_at
            FROM lhyy.{tableName}
            WHERE id = ANY(@ids)
              AND (integration_project_code IS NULL OR integration_project_code = @project_code)
            ORDER BY id
            FOR UPDATE;
            """, connection, transaction);
        AddRetrySqlParameters(command, ids, currentProjectCode);

        var result = new List<RetryMessageSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new RetryMessageSnapshot(
                new EsbMessage
                {
                    Id = reader.GetInt64(0),
                    MessageId = reader.GetString(1),
                    SourceMessageId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    TranCode = reader.GetString(3),
                    IntegrationProjectCode = reader.IsDBNull(4) ? null : reader.GetString(4),
                    RawJson = reader.GetString(5),
                    IdempotentKey = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Status = (MessageStatus)reader.GetInt16(7),
                    CreatedAt = reader.GetDateTime(8)
                },
                isArchive));
        }

        return result;
    }

    private static async Task RestoreArchiveMessagesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RetryMessageSnapshot[] messages,
        string? currentProjectCode,
        CancellationToken cancellationToken)
    {
        var ids = messages.Select(item => item.Message.Id).ToArray();
        var createdTimes = messages.Select(item => item.Message.CreatedAt).ToArray();
        await using var command = new NpgsqlCommand("""
            WITH selected(id, created_at) AS (
                SELECT *
                FROM unnest(@ids::bigint[], @created_times::timestamp[])
            ), moved AS (
                DELETE FROM lhyy.esb_messages_archive AS archive
                USING selected
                WHERE archive.id = selected.id
                  AND archive.created_at = selected.created_at
                  AND (archive.integration_project_code IS NULL OR archive.integration_project_code = @project_code)
                RETURNING
                    archive.id,
                    archive.message_id,
                    archive.source_message_id,
                    archive.tran_code,
                    archive.integration_project_code,
                    archive.tran_name,
                    archive.app_id,
                    archive.org_id,
                    archive.esb_timestamp,
                    archive.raw_json,
                    archive.body_json,
                    archive.idempotent_key,
                    archive.mrn,
                    archive.visit_no,
                    archive.inpatient_no,
                    archive.resolved_event_time,
                    archive.matched_rule_group,
                    archive.status,
                    archive.retry_count,
                    archive.error_message,
                    archive.patient_id,
                    archive.event_id,
                    archive.processed_at,
                    archive.processing_started_at,
                    archive.created_at
            )
            INSERT INTO lhyy.esb_messages (
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
            )
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
            FROM moved;
            """, connection, transaction);
        AddRetrySqlParameters(command, ids, currentProjectCode);
        command.Parameters.AddWithValue(
            "created_times",
            NpgsqlDbType.Array | NpgsqlDbType.Timestamp,
            createdTimes);

        var restoredCount = await command.ExecuteNonQueryAsync(cancellationToken);
        if (restoredCount != messages.Length)
            throw new InvalidOperationException("归档消息恢复数量不一致，已取消本批重试。");
    }

    private static async Task ResetMessagesForRetryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long[] ids,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE lhyy.esb_messages
            SET status = @pending_status,
                error_message = NULL,
                processed_at = NULL,
                processing_started_at = NULL
            WHERE id = ANY(@ids);
            """, connection, transaction);
        command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, ids);
        command.Parameters.AddWithValue("pending_status", NpgsqlDbType.Smallint, (short)MessageStatus.Pending);
        var updatedCount = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updatedCount != ids.Length)
            throw new InvalidOperationException("消息重置数量不一致，已取消本批重试。");
    }

    private static void AddRetrySqlParameters(
        NpgsqlCommand command,
        long[] ids,
        string? currentProjectCode)
    {
        command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, ids);
        command.Parameters.Add(new NpgsqlParameter("project_code", NpgsqlDbType.Varchar)
        {
            Value = string.IsNullOrWhiteSpace(currentProjectCode)
                ? DBNull.Value
                : currentProjectCode
        });
    }

    private static async Task ClearMessageReceiptsAsync(
        DataSyncDbContext db,
        IEnumerable<MessageReceiptKey> receiptKeys,
        CancellationToken cancellationToken)
    {
        foreach (var key in receiptKeys)
        {
            var sourceMessageId = string.IsNullOrWhiteSpace(key.SourceMessageId) ? null : key.SourceMessageId;
            var idempotentKey = string.IsNullOrWhiteSpace(key.IdempotentKey) ? null : key.IdempotentKey;
            if (sourceMessageId == null && idempotentKey == null)
                continue;

            var query = db.EsbMessageReceipts.Where(receipt =>
                receipt.IntegrationProjectCode == key.IntegrationProjectCode &&
                receipt.TranCode == key.TranCode);
            if (sourceMessageId != null && idempotentKey != null)
            {
                query = query.Where(receipt =>
                    receipt.SourceMessageId == sourceMessageId ||
                    receipt.IdempotentKey == idempotentKey);
            }
            else if (sourceMessageId != null)
            {
                query = query.Where(receipt => receipt.SourceMessageId == sourceMessageId);
            }
            else
            {
                query = query.Where(receipt => receipt.IdempotentKey == idempotentKey);
            }

            await query.ExecuteDeleteAsync(cancellationToken);
        }
    }

    private static async Task AcquireReceiptLocksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IEnumerable<MessageReceiptKey> receiptKeys,
        CancellationToken cancellationToken)
    {
        var lockKeys = receiptKeys
            .SelectMany(key => MessageReceiptService.BuildAdvisoryLockKeys(
                key.IntegrationProjectCode,
                key.TranCode,
                key.SourceMessageId,
                key.IdempotentKey))
            .Distinct()
            .OrderBy(key => key)
            .ToArray();
        if (lockKeys.Length == 0)
            return;

        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@lock_key);",
            connection,
            transaction);
        var parameter = command.Parameters.Add("lock_key", NpgsqlDbType.Bigint);
        foreach (var lockKey in lockKeys)
        {
            parameter.Value = lockKey;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static BatchRetrySkippedItem CreateSkippedItem(long id, string? messageId, string reason) => new()
    {
        Id = id,
        MessageId = messageId,
        Reason = reason
    };

    private async Task<bool> PrepareMessageForDirectProcessAsync(long id, CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var dbTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();
            await using (var lockCommand = new NpgsqlCommand("""
                SELECT id
                FROM lhyy.esb_messages
                WHERE id = @id
                  AND (integration_project_code IS NULL OR integration_project_code = @project_code)
                FOR UPDATE;
                """, connection, dbTransaction))
            {
                lockCommand.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                lockCommand.Parameters.Add(new NpgsqlParameter("project_code", NpgsqlDbType.Varchar)
                {
                    Value = string.IsNullOrWhiteSpace(currentProjectCode)
                        ? DBNull.Value
                        : currentProjectCode
                });
                if (await lockCommand.ExecuteScalarAsync(cancellationToken) == null)
                    return false;
            }

            var message = await db.EsbMessages.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
            if (message == null || !CanDirectProcess(message.Status))
                return false;

            var receiptKeys = await _receiverService.ResolveReplayReceiptKeysAsync(message, cancellationToken);
            await AcquireReceiptLocksAsync(connection, dbTransaction, receiptKeys, cancellationToken);
            await ClearMessageReceiptsAsync(db, receiptKeys, cancellationToken);

            message.Status = MessageStatus.Processing;
            message.ErrorMessage = null;
            message.ProcessedAt = null;
            message.ProcessingStartedAt = DateTime.Now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
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

    private sealed record RetryMessageSnapshot(EsbMessage Message, bool IsArchive);
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
