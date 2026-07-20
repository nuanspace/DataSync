using DataSync.LHYY.V2.Models.FollowUp;
using Microsoft.Extensions.Options;

namespace DataSync.LHYY.V2.Services.FollowUp;

public sealed class FollowUpPackageImportWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<FollowUpPackageImportOptions> options,
    ILogger<FollowUpPackageImportWorker> logger) : BackgroundService
{
    private readonly FollowUpPackageImportOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(_options.ScanIntervalSeconds, 30, 3600)));
        do
        {
            if (!_options.Enabled) continue;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<FollowUpPackageImportRepository>();
                if ((await repository.GetMissingTablesAsync(stoppingToken)).Count > 0) continue;
                var backupService = scope.ServiceProvider.GetRequiredService<FollowUpPackageBackupService>();
                if (!PreflightReady(backupService)) continue;
                await repository.DiscoverAsync(stoppingToken);
                if (await repository.HasUnsafeOperationAsync(stoppingToken))
                {
                    logger.LogCritical("检测到 FollowUp 恢复失败或中断中的危险操作状态，自动导入 Worker 已停止领取后续包，请转人工处置。");
                    continue;
                }
                var service = scope.ServiceProvider.GetRequiredService<FollowUpPackageImportService>();
                foreach (var package in await repository.GetPendingAsync(stoppingToken))
                {
                    if (package.PackageType == "Baseline") continue;
                    var result = await service.ImportAsync(package, false, stoppingToken);
                    if (!result.Success && package.PackageType is "Incremental" or "Replacement") break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "FollowUp 包导入 Worker 执行失败。"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private bool PreflightReady(FollowUpPackageBackupService backupService) =>
        Directory.Exists(_options.PackageRoot)
        && Directory.Exists(_options.StagingRoot)
        && Directory.Exists(_options.BackupRoot)
        && Directory.Exists(_options.AttachmentRoot)
        && File.Exists(_options.DecryptionPrivateKeyPath)
        && File.Exists(_options.CloudSigningPublicKeyPath)
        && backupService.PostgreSqlToolsReady;
}
