using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 后台服务：定期清理过期的处理日志和已完成的消息
/// </summary>
public class ProcessLogCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProcessLogCleanupService> _logger;

    public ProcessLogCleanupService(IServiceScopeFactory scopeFactory, ILogger<ProcessLogCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("日志清理服务已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            // 计算距离下次凌晨 2 点的等待时间
            var now = DateTime.Now;
            var nextRun = now.Date.AddHours(2);
            if (nextRun <= now)
                nextRun = nextRun.AddDays(1);

            var delay = nextRun - now;
            _logger.LogInformation("下次清理时间: {NextRun}（{Delay}后）", nextRun.ToString("yyyy-MM-dd HH:mm"), delay);

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
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理任务执行失败");
            }
        }

        _logger.LogInformation("日志清理服务已停止");
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var configService = scope.ServiceProvider.GetRequiredService<ConfigService>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DataSyncDbContext>>();

        // 读取保留天数配置（默认 30 天）
        var retentionDaysStr = await configService.GetGlobalConfigValueAsync("ProcessLogRetentionDays");
        var retentionDays = int.TryParse(retentionDaysStr, out var d) ? d : 30;
        var threshold = DateTime.Now.AddDays(-retentionDays);

        _logger.LogInformation("开始清理 {Days} 天前的数据（阈值: {Threshold}）", retentionDays, threshold.ToString("yyyy-MM-dd"));

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // 清理处理日志
        var logCount = await db.EsbProcessLogs
            .Where(l => l.CreatedAt < threshold)
            .ExecuteDeleteAsync(ct);

        // 清理已完成的消息（仅清理终态成功类，保留失败/待处理/处理中的）
        var msgCount = await db.EsbMessages
            .Where(m => m.CreatedAt < threshold
                && (m.Status == MessageStatus.Success
                    || m.Status == MessageStatus.Filtered
                    || m.Status == MessageStatus.Unmatched
                    || m.Status == MessageStatus.PartialSuccess))
            .ExecuteDeleteAsync(ct);

        var receiptCount = await db.EsbMessageReceipts
            .Where(r => r.CreatedAt < threshold)
            .ExecuteDeleteAsync(ct);

        _logger.LogInformation(
            "清理完成: 删除 {LogCount} 条处理日志, {MsgCount} 条已完成消息, {ReceiptCount} 条幂等回执",
            logCount,
            msgCount,
            receiptCount);
    }
}
