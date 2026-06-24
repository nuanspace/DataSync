using DataSync.CYYY.Services;

namespace DataSync.CYYY.Workers;

/// <summary>
/// Active 病历补采后台任务。
/// </summary>
public class ActiveMedicalRecordSyncWorker : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ActiveMedicalRecordSyncWorker> _logger;
    private readonly Dictionary<string, DateTime> _nextRunTimes = new(StringComparer.OrdinalIgnoreCase);

    public ActiveMedicalRecordSyncWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<ActiveMedicalRecordSyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Active 病历补采 Worker 已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueTasksAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Active 病历补采 Worker 执行出错");
            }

            try
            {
                await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Active 病历补采 Worker 已停止");
    }

    private async Task RunDueTasksAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ActiveSyncService>();
        var tasks = await service.GetEnabledTasksAsync(ct);
        var now = DateTime.Now;

        foreach (var task in tasks)
        {
            if (_nextRunTimes.TryGetValue(task.Code, out var nextRun) && nextRun > now)
                continue;

            var startedAt = DateTime.Now;
            await service.ExecuteTaskAsync(task, ct);

            var intervalSeconds = Math.Max(60, task.PollingIntervalSeconds);
            var next = startedAt.AddSeconds(intervalSeconds);
            if (next < DateTime.Now)
                next = DateTime.Now.AddSeconds(intervalSeconds);

            _nextRunTimes[task.Code] = next;
        }
    }
}
