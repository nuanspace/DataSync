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
            var intervalSeconds = Math.Max(60, task.PollingIntervalSeconds);
            try
            {
                await service.ExecuteTaskAsync(task, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    "Active 补采任务 [{TaskName}] 暂时无法连接病历接口，将在 {RetrySeconds} 秒后重试：{Message}",
                    task.Name,
                    intervalSeconds,
                    ex.Message);
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(
                    "Active 补采任务 [{TaskName}] 的病历接口响应格式错误，将在 {RetrySeconds} 秒后重试：{Message}",
                    task.Name,
                    intervalSeconds,
                    ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Active 补采任务 [{TaskName}] 执行失败", task.Name);
            }

            var next = startedAt.AddSeconds(intervalSeconds);
            if (next < DateTime.Now)
                next = DateTime.Now.AddSeconds(intervalSeconds);

            _nextRunTimes[task.Code] = next;
        }
    }
}
