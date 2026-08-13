using DataSync.CYYY.Services;

namespace DataSync.CYYY.Workers;

/// <summary>
/// 患者持续增量同步后台任务。
/// </summary>
public class PatientContinuousSyncWorker : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PatientContinuousSyncWorker> _logger;

    public PatientContinuousSyncWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<PatientContinuousSyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("患者持续同步 Worker 已启动");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<PatientContinuousSyncService>();
                var tasks = await service.GetEnabledTasksAsync(stoppingToken);
                foreach (var task in tasks)
                    await service.ExecuteTaskAsync(task, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "患者持续同步 Worker 执行出错");
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

        _logger.LogInformation("患者持续同步 Worker 已停止");
    }
}
