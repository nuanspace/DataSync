using System.Text.Json;
using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using DataSync.LHYY.V2.Services.FollowUp;
using Microsoft.EntityFrameworkCore;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 后台消息处理引擎：定时轮询 Pending + 可重试 Failed 消息
/// </summary>
public class MessageProcessingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MessageProcessingNotifier _notifier;
    private readonly ILogger<MessageProcessingService> _logger;
    private readonly FollowUpCubeOperationCoordinator _operationCoordinator;

    private const int DefaultIntervalMs = 5000;
    private const int DefaultMaxRetry = 3;
    private const int DefaultBatchSize = 50;
    private const int MaxBatchSize = 500;
    private const string BatchSizeConfigKey = "MessageProcessingBatchSize";

    public MessageProcessingService(
        IServiceScopeFactory scopeFactory,
        MessageProcessingNotifier notifier,
        ILogger<MessageProcessingService> logger,
        FollowUpCubeOperationCoordinator operationCoordinator)
    {
        _scopeFactory = scopeFactory;
        _notifier = notifier;
        _logger = logger;
        _operationCoordinator = operationCoordinator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("消息处理引擎已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int processed;
                await using (var operationLease = await _operationCoordinator.TryAcquireSharedAsync(stoppingToken))
                {
                    if (operationLease is null)
                    {
                        await _notifier.WaitAsync(TimeSpan.FromMilliseconds(DefaultIntervalMs), stoppingToken);
                        continue;
                    }
                    processed = await ProcessBatchAsync(stoppingToken);
                }
                if (processed == 0)
                {
                    var intervalMs = await GetIntervalMsAsync();
                    await _notifier.WaitAsync(TimeSpan.FromMilliseconds(intervalMs), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "消息处理循环发生错误，5秒后重试");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("消息处理引擎已停止");
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DataSyncDbContext>>();
        var integrationProjectService = scope.ServiceProvider.GetRequiredService<IntegrationProjectService>();
        var currentProjectCode = await integrationProjectService.GetCurrentProjectCodeAsync();
        var configService = scope.ServiceProvider.GetRequiredService<ConfigService>();

        var maxRetry = int.TryParse(await configService.GetGlobalConfigValueAsync("MaxRetryCount"), out var mr) ? mr : DefaultMaxRetry;
        var batchSize = await GetBatchSizeAsync(configService);

        await using (var resetDb = await dbFactory.CreateDbContextAsync(ct))
        {
            var staleThreshold = DateTime.Now.AddMinutes(-5);
            var staleCount = await resetDb.EsbMessages
                .WhereInProjectOrGlobal(currentProjectCode)
                .Where(m => m.Status == MessageStatus.Processing && m.ProcessingStartedAt != null && m.ProcessingStartedAt < staleThreshold)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, MessageStatus.Failed)
                    .SetProperty(m => m.ErrorMessage, "处理超时，已自动重置")
                    .SetProperty(m => m.RetryCount, m => m.RetryCount + 1), ct);
            if (staleCount > 0)
                _logger.LogWarning("已重置 {Count} 条超时 Processing 消息", staleCount);
        }

        var messages = await ClaimMessagesAsync(dbFactory, currentProjectCode, maxRetry, batchSize, ct);
        if (messages.Count == 0) return 0;

        int count = 0;
        foreach (var message in messages)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await ProcessSingleMessageAsync(scope.ServiceProvider, message, ct);
                count++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理消息 {MessageId}(ID:{Id}) 失败", message.MessageId, message.Id);
            }
        }

        return count;
    }

    private static async Task<List<EsbMessage>> ClaimMessagesAsync(
        IDbContextFactory<DataSyncDbContext> dbFactory,
        string currentProjectCode,
        int maxRetry,
        int batchSize,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.Now;
        var pending = (short)MessageStatus.Pending;
        var processing = (short)MessageStatus.Processing;
        var failed = (short)MessageStatus.Failed;

        return await db.EsbMessages
            .FromSqlInterpolated($@"
                UPDATE lhyy.esb_messages AS m
                SET status = {processing},
                    processing_started_at = {now}
                WHERE m.id IN (
                    SELECT id
                    FROM lhyy.esb_messages
                    WHERE (integration_project_code = {currentProjectCode} OR integration_project_code IS NULL)
                      AND (status = {pending} OR (status = {failed} AND retry_count < {maxRetry}))
                    ORDER BY created_at
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING m.*")
            .AsNoTracking()
            .ToListAsync(ct);
    }

    private async Task ProcessSingleMessageAsync(IServiceProvider sp, EsbMessage message, CancellationToken ct)
    {
        var receiverService = sp.GetRequiredService<EsbReceiverService>();
        await receiverService.ProcessQueuedMessageAsync(message.Id, ct);
    }

    private async Task UpdateMessageStatus(IDbContextFactory<DataSyncDbContext> dbFactory, long id, MessageStatus status, string? error)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var message = await db.EsbMessages.FindAsync(id);
        if (message != null)
        {
            message.Status = status;
            message.ErrorMessage = error;
            message.ProcessedAt = DateTime.Now;
            if (status == MessageStatus.Failed)
                message.RetryCount++;
            if (status == MessageStatus.Pending)
                message.ProcessingStartedAt = null;

            await db.SaveChangesAsync();
        }
    }

    private async Task<int> GetIntervalMsAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var configService = scope.ServiceProvider.GetRequiredService<ConfigService>();
        var value = await configService.GetGlobalConfigValueAsync("ProcessingIntervalMs");
        return int.TryParse(value, out var intervalMs) ? intervalMs : DefaultIntervalMs;
    }

    private static async Task<int> GetBatchSizeAsync(ConfigService configService)
    {
        var value = await configService.GetGlobalConfigValueAsync(BatchSizeConfigKey);
        if (!int.TryParse(value, out var batchSize) || batchSize <= 0)
            return DefaultBatchSize;

        return Math.Min(batchSize, MaxBatchSize);
    }
}
