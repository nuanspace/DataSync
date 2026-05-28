using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 后台服务：定期将过期终态消息和处理日志归档到历史分区表。
/// </summary>
public class ProcessLogCleanupService : BackgroundService
{
    private const int DefaultHotRetentionDays = 30;
    private const int DefaultCleanupRetentionDays = 30;
    private const int DefaultBatchSize = 50000;
    private const long ArchiveLockKey = 2026052601;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProcessLogCleanupService> _logger;

    public ProcessLogCleanupService(IServiceScopeFactory scopeFactory, ILogger<ProcessLogCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("消息归档服务已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var nextRun = now.Date.AddHours(2);
            if (nextRun <= now)
                nextRun = nextRun.AddDays(1);

            var delay = nextRun - now;
            _logger.LogInformation("下次归档时间: {NextRun}（{Delay}后）", nextRun.ToString("yyyy-MM-dd HH:mm"), delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await ArchiveAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "消息归档任务执行失败");
            }
        }

        _logger.LogInformation("消息归档服务已停止");
    }

    private async Task ArchiveAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var configService = scope.ServiceProvider.GetRequiredService<ConfigService>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DataSyncDbContext>>();

        var hotRetentionDaysStr = await configService.GetGlobalConfigValueAsync("MessageHotRetentionDays");
        var hotRetentionDays = ParseRetentionDays(hotRetentionDaysStr, DefaultHotRetentionDays);
        var archiveThreshold = DateTime.Now.AddDays(-hotRetentionDays);
        var cleanupRetentionDaysStr = await configService.GetGlobalConfigValueAsync("ProcessLogRetentionDays");
        var cleanupRetentionDays = ParseRetentionDays(cleanupRetentionDaysStr, DefaultCleanupRetentionDays);
        var cleanupThreshold = DateTime.Now.AddDays(-cleanupRetentionDays);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connectionString = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("未找到 DataSyncDb 连接字符串，无法执行消息归档。");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var archiveState = await ArchiveOptimizationCheck.GetStateAsync(connection, ct);
        if (archiveState == ArchiveOptimizationState.NotInstalled)
        {
            _logger.LogInformation("未安装 ESB 消息归档优化结构，执行旧版清理逻辑。");
            await CleanupLegacyAsync(db, cleanupThreshold, ct);
            return;
        }

        if (archiveState != ArchiveOptimizationState.Ready)
        {
            _logger.LogWarning("ESB 消息归档优化结构未完整就绪，本次跳过消息归档和旧版消息日志清理。");
            await CleanupReceiptsAsync(db, cleanupThreshold, ct);
            return;
        }

        if (!await TryAcquireArchiveLockAsync(connection, ct))
        {
            _logger.LogInformation("已有归档任务正在执行，本次后台归档跳过。");
            await CleanupReceiptsAsync(db, cleanupThreshold, ct);
            return;
        }

        try
        {
            long totalMessages = 0;
            long totalLogs = 0;
            while (!ct.IsCancellationRequested)
            {
                var result = await ArchiveBatchAsync(connection, archiveThreshold, DefaultBatchSize, ct);
                if (result.ProcessedMessageCount == 0)
                    break;

                totalMessages += result.ArchivedMessageCount;
                totalLogs += result.ArchivedLogCount;
                _logger.LogInformation(
                    "消息归档批次完成：处理消息 {ProcessedMessageCount} 条，归档消息 {ArchivedMessageCount} 条，处理日志 {ArchivedLogCount} 条",
                    result.ProcessedMessageCount,
                    result.ArchivedMessageCount,
                    result.ArchivedLogCount);
            }

            _logger.LogInformation(
                "消息归档完成：阈值 {Threshold}，归档消息 {MessageCount} 条，归档处理日志 {LogCount} 条",
                archiveThreshold.ToString("yyyy-MM-dd HH:mm:ss"),
                totalMessages,
                totalLogs);
        }
        finally
        {
            await ReleaseArchiveLockAsync(connection);
            await CleanupReceiptsAsync(db, cleanupThreshold, ct);
        }
    }

    private async Task CleanupLegacyAsync(DataSyncDbContext db, DateTime threshold, CancellationToken ct)
    {
        var logCount = await db.EsbProcessLogs
            .Where(l => l.CreatedAt < threshold)
            .ExecuteDeleteAsync(ct);

        var msgCount = await db.EsbMessages
            .Where(m => m.CreatedAt < threshold
                && (m.Status == MessageStatus.Success
                    || m.Status == MessageStatus.Filtered
                    || m.Status == MessageStatus.Unmatched
                    || m.Status == MessageStatus.PartialSuccess))
            .ExecuteDeleteAsync(ct);

        var receiptCount = await CleanupReceiptsAsync(db, threshold, ct);

        _logger.LogInformation(
            "旧版清理完成：删除 {LogCount} 条处理日志，{MsgCount} 条已完成消息，{ReceiptCount} 条幂等回执",
            logCount,
            msgCount,
            receiptCount);
    }

    private async Task<int> CleanupReceiptsAsync(DataSyncDbContext db, DateTime threshold, CancellationToken ct)
    {
        var receiptCount = await db.EsbMessageReceipts
            .Where(r => r.CreatedAt < threshold)
            .ExecuteDeleteAsync(ct);

        _logger.LogInformation(
            "幂等回执清理完成：阈值 {Threshold}，删除 {ReceiptCount} 条",
            threshold.ToString("yyyy-MM-dd HH:mm:ss"),
            receiptCount);

        return receiptCount;
    }

    private static int ParseRetentionDays(string? value, int fallback) =>
        int.TryParse(value, out var days) && days > 0 ? days : fallback;

    private static async Task<ArchiveBatchResult> ArchiveBatchAsync(
        NpgsqlConnection connection,
        DateTime threshold,
        int batchSize,
        CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await ExecuteNonQueryAsync(connection, transaction, """
                CREATE TEMP TABLE tmp_esb_archive_batch (
                    id BIGINT PRIMARY KEY,
                    created_at TIMESTAMP NOT NULL
                ) ON COMMIT DROP;
                """, ct);

            await ExecuteNonQueryAsync(connection, transaction, """
                INSERT INTO tmp_esb_archive_batch (id, created_at)
                SELECT id, created_at
                FROM lhyy.esb_messages
                WHERE created_at < @threshold
                  AND status IN (2, 4, 5, 6)
                ORDER BY created_at, id
                LIMIT @batchSize
                FOR UPDATE SKIP LOCKED;
                """, ct, ("threshold", threshold), ("batchSize", batchSize));

            var batchCount = await ExecuteScalarLongAsync(connection, transaction, "SELECT COUNT(*) FROM tmp_esb_archive_batch;", ct);
            if (batchCount == 0)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new ArchiveBatchResult(0, 0, 0);
            }

            var months = await LoadArchiveMonthsAsync(connection, transaction, ct);
            foreach (var month in months)
            {
                await ExecuteNonQueryAsync(
                    connection,
                    transaction,
                    "SELECT lhyy.ensure_esb_archive_partition(CAST(@month AS date));",
                    ct,
                    ("month", month));
            }

            var insertedMessages = await ExecuteNonQueryAsync(connection, transaction, """
                INSERT INTO lhyy.esb_messages_archive (
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
                    created_at,
                    archived_at
                )
                SELECT
                    m.id,
                    m.message_id,
                    m.source_message_id,
                    m.tran_code,
                    m.integration_project_code,
                    m.tran_name,
                    m.app_id,
                    m.org_id,
                    m.esb_timestamp,
                    m.raw_json,
                    m.body_json,
                    m.idempotent_key,
                    m.mrn,
                    m.visit_no,
                    m.inpatient_no,
                    m.resolved_event_time,
                    m.matched_rule_group,
                    m.status,
                    m.retry_count,
                    m.error_message,
                    m.patient_id,
                    m.event_id,
                    m.processed_at,
                    m.processing_started_at,
                    m.created_at,
                    NOW()
                FROM lhyy.esb_messages m
                INNER JOIN tmp_esb_archive_batch b ON b.id = m.id
                WHERE TRUE
                ON CONFLICT DO NOTHING;
                """, ct);

            var insertedLogs = await ExecuteNonQueryAsync(connection, transaction, """
                INSERT INTO lhyy.esb_process_log_archive (
                    id,
                    message_id,
                    integration_project_code,
                    step,
                    is_success,
                    detail,
                    elapsed_ms,
                    created_at,
                    archived_at
                )
                SELECT
                    l.id,
                    l.message_id,
                    l.integration_project_code,
                    l.step,
                    l.is_success,
                    l.detail,
                    l.elapsed_ms,
                    l.created_at,
                    NOW()
                FROM lhyy.esb_process_log l
                INNER JOIN tmp_esb_archive_batch b ON b.id = l.message_id
                WHERE TRUE
                ON CONFLICT DO NOTHING;
                """, ct);

            await ExecuteNonQueryAsync(connection, transaction, """
                DELETE FROM lhyy.esb_process_log l
                USING tmp_esb_archive_batch b
                WHERE b.id = l.message_id
                  AND EXISTS (
                      SELECT 1
                      FROM lhyy.esb_messages_archive a
                      WHERE a.id = b.id
                        AND a.created_at = b.created_at
                  );
                """, ct);

            await ExecuteNonQueryAsync(connection, transaction, """
                DELETE FROM lhyy.esb_messages m
                USING tmp_esb_archive_batch b
                WHERE b.id = m.id
                  AND EXISTS (
                      SELECT 1
                      FROM lhyy.esb_messages_archive a
                      WHERE a.id = b.id
                        AND a.created_at = b.created_at
                  );
                """, ct);

            await transaction.CommitAsync(ct);
            return new ArchiveBatchResult(batchCount, insertedMessages, insertedLogs);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<List<DateTime>> LoadArchiveMonthsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        var result = new List<DateTime>();
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT date_trunc('month', month_value)::date
            FROM (
                SELECT m.created_at AS month_value
                FROM tmp_esb_archive_batch m
                UNION
                SELECT l.created_at AS month_value
                FROM lhyy.esb_process_log l
                INNER JOIN tmp_esb_archive_batch b ON b.id = l.message_id
            ) s
            ORDER BY 1;
            """, connection, transaction);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetDateTime(0));

        return result;
    }

    private static async Task<int> ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var command = BuildCommand(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ExecuteScalarLongAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var command = BuildCommand(connection, transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value);
    }

    private static NpgsqlCommand BuildCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static async Task<bool> TryAcquireArchiveLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@lockKey);", connection);
        command.Parameters.AddWithValue("lockKey", ArchiveLockKey);
        var value = await command.ExecuteScalarAsync(ct);
        return value is bool acquired && acquired;
    }

    private static async Task ReleaseArchiveLockAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@lockKey);", connection);
        command.Parameters.AddWithValue("lockKey", ArchiveLockKey);
        await command.ExecuteScalarAsync();
    }

    private readonly record struct ArchiveBatchResult(
        long ProcessedMessageCount,
        long ArchivedMessageCount,
        long ArchivedLogCount);
}
