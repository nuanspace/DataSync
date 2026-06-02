using DataSync.CYYY.Models;
using DataSync.CYYY.Services;

namespace DataSync.CYYY.Workers;

/// <summary>
/// 采集后台任务 — 定时从数据湖拉取数据到本地采集表，定期刷新配置
/// </summary>
public class IngestionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IngestionWorker> _logger;

    /// <summary>
    /// 运行中的采集循环：ServerCode → (取消令牌源, 循环Task)
    /// </summary>
    private readonly Dictionary<string, (CancellationTokenSource Cts, Task Loop)> _runningSources = [];

    /// <summary>
    /// 配置刷新间隔（秒）
    /// </summary>
    private const int ConfigReloadIntervalSeconds = 60;

    public IngestionWorker(IServiceScopeFactory scopeFactory, ILogger<IngestionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 等待数据湖配置就绪
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (await CanStartWithoutWaitingDataLakeAsync(stoppingToken))
                        break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "检查数据湖配置时出错");
                }
                _logger.LogWarning("数据湖未配置，IngestionWorker 等待中...");
                await Task.Delay(TimeSpan.FromSeconds(ConfigReloadIntervalSeconds), stoppingToken);
            }
        }

        _logger.LogInformation("IngestionWorker 已启动，每 {Interval} 秒刷新采集源配置",
            ConfigReloadIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshSourcesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IngestionWorker 刷新采集源配置出错");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(ConfigReloadIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // 停止所有运行中的采集循环
        foreach (var (_, entry) in _runningSources)
        {
            entry.Cts.Cancel();
            entry.Cts.Dispose();
        }
        _runningSources.Clear();
    }

    /// <summary>
    /// 定期刷新：对比数据库配置与运行中的循环，动态启停
    /// </summary>
    private async Task RefreshSourcesAsync(CancellationToken ct)
    {
        List<IngestionSource> enabledSources;
        using (var scope = _scopeFactory.CreateScope())
        {
            var ingestionService = scope.ServiceProvider.GetRequiredService<IngestionService>();
            enabledSources = await ingestionService.GetEnabledSourcesAsync(ct);
        }
        var enabledCodes = enabledSources.Select(s => s.ServerCode).ToHashSet();

        // 清理已自行退出的循环
        var completedCodes = _runningSources
            .Where(kv => kv.Value.Loop.IsCompleted)
            .Select(kv => kv.Key).ToList();
        foreach (var code in completedCodes)
        {
            _runningSources[code].Cts.Dispose();
            _runningSources.Remove(code);
        }

        // 停止已禁用/删除的采集源
        var toStop = _runningSources.Keys.Where(c => !enabledCodes.Contains(c)).ToList();
        foreach (var code in toStop)
        {
            _logger.LogInformation("采集源 [{ServerCode}] 已禁用或删除，停止轮询", code);
            _runningSources[code].Cts.Cancel();
            _runningSources[code].Cts.Dispose();
            _runningSources.Remove(code);
        }

        // 启动新增的采集源
        foreach (var source in enabledSources)
        {
            if (!_runningSources.ContainsKey(source.ServerCode))
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var loop = RunIngestionLoopAsync(source.ServerCode, cts.Token);
                _runningSources[source.ServerCode] = (cts, loop);
                _logger.LogInformation("采集源 [{Name}]({ServerCode}) 轮询循环已启动",
                    source.Name, source.ServerCode);
            }
        }
    }

    private async Task RunIngestionLoopAsync(string serverCode, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IngestionSource? freshSource = null;
            var startedAt = DateTime.Now;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var ingestionService = scope.ServiceProvider.GetRequiredService<IngestionService>();

                // 每轮重新加载采集源配置（轮询间隔等可能有变动）
                freshSource = await ingestionService.GetSourceByServerCodeAsync(serverCode, ct);
                if (freshSource == null || !freshSource.Enabled)
                {
                    _logger.LogWarning("采集源 [{ServerCode}] 已禁用或被删除，停止轮询", serverCode);
                    return;
                }

                await ingestionService.IngestAsync(freshSource, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "采集源 [{ServerCode}] 执行出错", serverCode);
            }

            try
            {
                var interval = freshSource?.PollingIntervalSeconds ?? 300;
                var waitTime = TimeSpan.FromSeconds(interval) - (DateTime.Now - startedAt);
                if (waitTime < TimeSpan.Zero)
                    waitTime = TimeSpan.Zero;

                await Task.Delay(waitTime, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("采集源 [{ServerCode}] 轮询循环已停止", serverCode);
    }

    private async Task<bool> CanStartWithoutWaitingDataLakeAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var ingestionService = scope.ServiceProvider.GetRequiredService<IngestionService>();
        var sources = await ingestionService.GetEnabledSourcesAsync(ct);
        if (sources.Count == 0 || sources.All(IngestionService.IsDatabaseSource))
            return true;

        var dlClient = scope.ServiceProvider.GetRequiredService<DataLakeClient>();
        return await dlClient.HasConfigAsync(ct);
    }
}
