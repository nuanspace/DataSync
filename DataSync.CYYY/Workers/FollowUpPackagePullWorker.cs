using DataSync.CYYY.Services.FollowUp;
using DataSync.CYYY.Models.FollowUp;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace DataSync.CYYY.Workers;

public sealed class FollowUpPackagePullWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<FollowUpPackageSyncOptions> options,
    ILogger<FollowUpPackagePullWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRuns = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("FollowUp 包自动拉取 Worker 未启用。");
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<FollowUpPackageRepository>();
                if ((await repository.GetMissingTablesAsync(stoppingToken)).Count > 0) continue;
                var keyService = scope.ServiceProvider.GetRequiredService<FollowUpPackageKeyService>();
                if (!keyService.GetPreflight().Ready) continue;
                var service = scope.ServiceProvider.GetRequiredService<FollowUpPackageSyncService>();
                foreach (var source in await repository.GetSourcesAsync(true, stoppingToken))
                {
                    var now = DateTimeOffset.Now;
                    if (_lastRuns.TryGetValue(source.HospitalCode, out var last)
                        && now - last < TimeSpan.FromSeconds(Math.Clamp(source.PullIntervalSeconds, 30, 86400)))
                        continue;
                    _lastRuns[source.HospitalCode] = now;
                    _ = await service.SyncSourceAsync(source, false, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "FollowUp 包拉取 Worker 执行失败。");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
