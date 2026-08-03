using DataSync.CYYY.Models;
using DataSync.CYYY.Services;

namespace DataSync.CYYY.Workers;

/// <summary>
/// 通用同步 Worker — 从 sync_tasks 表驱动所有任务，定期刷新配置
/// </summary>
public class SyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly SyncTaskSignalService _taskSignalService;
    private readonly ILogger<SyncWorker> _logger;

    /// <summary>
    /// 运行中的任务循环：TaskCode → (取消令牌源, 循环Task)
    /// </summary>
    private readonly Dictionary<string, (CancellationTokenSource Cts, Task Loop)> _runningTasks = [];

    /// <summary>
    /// 配置刷新间隔（秒）
    /// </summary>
    private const int ConfigReloadIntervalSeconds = 60;

    /// <summary>
    /// 上次日志清理时间
    /// </summary>
    private DateTime _lastCleanupTime = DateTime.MinValue;

    /// <summary>
    /// 运行中对象过期时间，避免进程异常后长期卡死
    /// </summary>
    private static readonly TimeSpan RunningLeaseTimeout = TimeSpan.FromMinutes(10);

    public SyncWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        SyncTaskSignalService taskSignalService,
        ILogger<SyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _taskSignalService = taskSignalService;
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
                _logger.LogWarning("数据湖未配置，SyncWorker 等待中...");
                await Task.Delay(TimeSpan.FromSeconds(ConfigReloadIntervalSeconds), stoppingToken);
            }
        }

        _logger.LogInformation("SyncWorker 已启动，每 {Interval} 秒刷新任务配置",
            ConfigReloadIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshTasksAsync(stoppingToken);
                await CleanupLogsIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SyncWorker 刷新任务配置出错");
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

        // 停止所有运行中的任务循环
        foreach (var (_, entry) in _runningTasks)
        {
            entry.Cts.Cancel();
            entry.Cts.Dispose();
        }
        _runningTasks.Clear();
    }

    /// <summary>
    /// 定期刷新：对比数据库配置与运行中的循环，动态启停
    /// </summary>
    private async Task RefreshTasksAsync(CancellationToken ct)
    {
        List<SyncTask> enabledTasks;
        using (var scope = _scopeFactory.CreateScope())
        {
            var logService = scope.ServiceProvider.GetRequiredService<SyncLogService>();
            enabledTasks = await logService.GetEnabledTasksAsync(ct);
        }
        var enabledCodes = enabledTasks.Select(t => t.Code).ToHashSet();

        // 清理已自行退出的循环（任务在循环内检测到禁用后自动退出）
        var completedCodes = _runningTasks
            .Where(kv => kv.Value.Loop.IsCompleted)
            .Select(kv => kv.Key).ToList();
        foreach (var code in completedCodes)
        {
            _runningTasks[code].Cts.Dispose();
            _runningTasks.Remove(code);
        }

        // 停止已禁用/删除的任务
        var toStop = _runningTasks.Keys.Where(c => !enabledCodes.Contains(c)).ToList();
        foreach (var code in toStop)
        {
            _logger.LogInformation("任务 [{TaskCode}] 已禁用或删除，停止轮询", code);
            _runningTasks[code].Cts.Cancel();
            _runningTasks[code].Cts.Dispose();
            _runningTasks.Remove(code);
        }

        // 启动新增的任务
        foreach (var task in enabledTasks)
        {
            if (!_runningTasks.ContainsKey(task.Code))
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var loop = RunTaskLoopAsync(task.Code, cts.Token);
                _runningTasks[task.Code] = (cts, loop);
                _logger.LogInformation("任务 [{TaskName}]({TaskCode}) 轮询循环已启动",
                    task.Name, task.Code);
            }
        }
    }

    private async Task RunTaskLoopAsync(string taskCode, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SyncTask? freshTask = null;
            var startedAt = DateTime.Now;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var logService = scope.ServiceProvider.GetRequiredService<SyncLogService>();
                var orchestrator = scope.ServiceProvider.GetRequiredService<SyncOrchestrator>();
                var ingestionService = scope.ServiceProvider.GetRequiredService<IngestionService>();
                var localQueryService = scope.ServiceProvider.GetRequiredService<LocalQueryService>();
                var pendingSyncService = scope.ServiceProvider.GetRequiredService<PendingSyncService>();

                // 每轮重新加载任务配置（接口列表、轮询间隔等可能有变动）
                freshTask = await logService.GetTaskByCodeAsync(taskCode, ct);
                if (freshTask == null || !freshTask.Enabled)
                {
                    _logger.LogWarning("任务 [{TaskCode}] 已禁用或被删除，停止轮询", taskCode);
                    return;
                }

                var source = await ingestionService.GetSourceByServerCodeAsync(freshTask.TriggerServerCode, ct);
                if (source == null)
                {
                    _logger.LogWarning(
                        "任务 [{TaskCode}] 对应的采集源 [{ServerCode}] 不存在，跳过本轮处理",
                        taskCode, freshTask.TriggerServerCode);
                    await logService.UpdateCheckpointAsync(taskCode, DateTime.Now, ct, 0);
                }
                else
                {
                    var batchSize = Math.Max(1, Math.Min(100,
                        freshTask.PatientConcurrency * Math.Max(1, freshTask.ApiConcurrency)));
                    var pendingItems = await pendingSyncService.LeaseDueItemsAsync(
                        taskCode, batchSize, RunningLeaseTimeout, ct);

                    var successCount = 0;
                    var failCount = 0;
                    var waitingCount = 0;
                    foreach (var pendingItem in pendingItems)
                    {
                        var snapshotRecord = PendingSyncService.DeserializeTriggerRecord(pendingItem.TriggerRecordJson);
                        if (snapshotRecord == null)
                        {
                            failCount++;
                            await pendingSyncService.MarkFailedAsync(
                                pendingItem.Id,
                                "触发记录快照反序列化失败",
                                TimeSpan.FromSeconds(freshTask.PollingIntervalSeconds),
                                ct);
                            continue;
                        }

                        try
                        {
                            var triggerRecord = await localQueryService.QueryMatchingTriggerRecordAsync(
                                freshTask, source, snapshotRecord, ct);
                            if (triggerRecord == null)
                            {
                                var unfilteredRecord = await localQueryService.QueryMatchingTriggerRecordAsync(
                                    freshTask, source, snapshotRecord, ct, skipRules: true);
                                if (unfilteredRecord != null)
                                {
                                    await pendingSyncService.MarkSkippedAsync(
                                        pendingItem.Id,
                                        "未满足任务过滤条件",
                                        ct);
                                }
                                else
                                {
                                    waitingCount++;
                                    await pendingSyncService.MarkWaitingAsync(
                                        pendingItem.Id,
                                        "同步条件未满足，等待依赖数据",
                                        TimeSpan.FromSeconds(freshTask.PollingIntervalSeconds),
                                        ct);
                                }
                                continue;
                            }

                            if (freshTask.EnableTriggerRecordPush && !pendingItem.TriggerPushDone)
                            {
                                try
                                {
                                    await orchestrator.PushTriggerRecordAsync(
                                        freshTask,
                                        triggerRecord,
                                        "Scheduled",
                                        ct,
                                        snapshotRecord,
                                        pendingItem.SourceRecordKey);
                                    await pendingSyncService.MarkTriggerPushSuccessAsync(pendingItem.Id, ct);
                                }
                                catch (Exception ex)
                                {
                                    failCount++;
                                    await pendingSyncService.MarkTriggerPushFailedAsync(
                                        pendingItem.Id,
                                        ex.Message,
                                        TimeSpan.FromSeconds(freshTask.PollingIntervalSeconds),
                                        ct);
                                    _logger.LogError(ex,
                                        "任务 [{TaskCode}] 触发记录单独推送失败，记录键 {SourceRecordKey}",
                                        taskCode, pendingItem.SourceRecordKey);
                                    continue;
                                }
                            }

                            var completedKeys = pendingItem.CompletedInterfaceKeySet;
                            var remainingInterfaces = GetRemainingTopLevelInterfaces(freshTask, completedKeys);
                            if (remainingInterfaces.Count == 0)
                            {
                                successCount++;
                                await pendingSyncService.MarkSuccessAsync(pendingItem.Id, ct);
                                continue;
                            }

                            var now = DateTime.Now;
                            var dueInterfaces = remainingInterfaces
                                .Where(iface => IsExecutionUnitOpen(freshTask, iface, now))
                                .ToList();
                            if (dueInterfaces.Count == 0)
                            {
                                waitingCount++;
                                await pendingSyncService.MarkDeferredAsync(
                                    pendingItem.Id,
                                    GetNextExecutionTime(freshTask, remainingInterfaces, now),
                                    ct);
                                continue;
                            }

                            var result = await orchestrator.ExecuteSyncAsync(
                                freshTask,
                                [triggerRecord],
                                "Scheduled",
                                ct,
                                skipTriggerRecordPush: freshTask.EnableTriggerRecordPush,
                                sourceRecordKey: pendingItem.SourceRecordKey,
                                selectedInterfaceIds: dueInterfaces.Select(iface => iface.Id).ToList());

                            if (result.CompletedInterfaceKeys.Count > 0)
                            {
                                completedKeys.UnionWith(result.CompletedInterfaceKeys);
                                await pendingSyncService.SaveCompletedInterfacesAsync(
                                    pendingItem.Id,
                                    result.CompletedInterfaceKeys,
                                    ct);
                            }

                            if (result.FailCount > 0)
                            {
                                failCount++;
                                var errorMessage = result.FailDetails.FirstOrDefault()?.ErrorMessage ?? "同步对象未完成";
                                await pendingSyncService.MarkFailedAsync(
                                    pendingItem.Id,
                                    errorMessage,
                                    TimeSpan.FromSeconds(freshTask.PollingIntervalSeconds),
                                    ct);
                                continue;
                            }

                            remainingInterfaces = GetRemainingTopLevelInterfaces(freshTask, completedKeys);
                            if (remainingInterfaces.Count == 0)
                            {
                                successCount++;
                                await pendingSyncService.MarkSuccessAsync(pendingItem.Id, ct);
                            }
                            else
                            {
                                waitingCount++;
                                await pendingSyncService.MarkDeferredAsync(
                                    pendingItem.Id,
                                    GetNextExecutionTime(freshTask, remainingInterfaces, DateTime.Now),
                                    ct);
                            }
                        }
                        catch (Exception ex)
                        {
                            failCount++;
                            await pendingSyncService.MarkFailedAsync(
                                pendingItem.Id,
                                ex.Message,
                                TimeSpan.FromSeconds(freshTask.PollingIntervalSeconds),
                                ct);
                            _logger.LogError(ex,
                                "任务 [{TaskCode}] 处理待同步对象失败，记录键 {SourceRecordKey}",
                                taskCode, pendingItem.SourceRecordKey);
                        }
                    }

                    if (pendingItems.Count > 0)
                    {
                        _logger.LogInformation(
                            "任务 [{TaskName}] 本轮处理 {Count} 个待同步对象：成功 {Success}，失败 {Fail}，等待条件 {Waiting}",
                            freshTask.Name, pendingItems.Count, successCount, failCount, waitingCount);

                        if (successCount > 0 || failCount > 0)
                        {
                            await logService.UpdatePushCheckpointAsync(
                                taskCode, successCount, failCount, ct);
                        }
                    }

                    await logService.UpdateCheckpointAsync(taskCode, DateTime.Now, ct, pendingItems.Count);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "任务 [{TaskCode}] 执行出错", taskCode);
            }

            try
            {
                var interval = freshTask?.PollingIntervalSeconds ?? 30;
                var waitTime = TimeSpan.FromSeconds(interval) - (DateTime.Now - startedAt);
                if (waitTime < TimeSpan.Zero)
                    waitTime = TimeSpan.Zero;

                await _taskSignalService.WaitAsync(taskCode, waitTime, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("任务 [{TaskCode}] 轮询循环已停止", taskCode);
    }

    private async Task<bool> CanStartWithoutWaitingDataLakeAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var logService = scope.ServiceProvider.GetRequiredService<SyncLogService>();
        var tasks = await logService.GetEnabledTasksAsync(ct);
        if (tasks.Count == 0)
            return true;

        if (tasks.Any(t => t.Interfaces.Any(i => i.Enabled && IsDataLakeInterface(i))))
        {
            var dlClient = scope.ServiceProvider.GetRequiredService<DataLakeClient>();
            if (!await dlClient.HasConfigAsync(ct))
                return false;
        }

        if (tasks.Any(t => t.Interfaces.Any(i => i.Enabled && IsDynamicApiInterface(i))))
        {
            var dynamicApiClient = scope.ServiceProvider.GetRequiredService<DynamicApiClient>();
            if (!await dynamicApiClient.HasConfigAsync(ct))
                return false;
        }

        return true;
    }

    private static bool IsDataLakeInterface(SyncTaskInterface iface) =>
        string.Equals(
            IngestionService.NormalizeSourceType(iface.SourceType),
            IngestionService.SourceTypeDataLake,
            StringComparison.Ordinal);

    private static bool IsDynamicApiInterface(SyncTaskInterface iface) =>
        IngestionService.IsDynamicApiSourceType(iface.SourceType);

    private static List<SyncTaskInterface> GetRemainingTopLevelInterfaces(
        SyncTask task,
        HashSet<string> completedKeys)
        => task.Interfaces
            .Where(iface => iface.Enabled && string.IsNullOrWhiteSpace(iface.ParentInterfaceKey))
            .Where(iface => !completedKeys.Contains(InterfaceAccessWindow.GetProgressKey(iface)))
            .OrderBy(iface => iface.SortOrder)
            .ToList();

    private static bool IsExecutionUnitOpen(SyncTask task, SyncTaskInterface root, DateTime now)
        => GetExecutionUnit(task, root).All(iface => InterfaceAccessWindow.IsOpen(iface, now));

    private static DateTime GetNextExecutionTime(
        SyncTask task,
        IEnumerable<SyncTaskInterface> roots,
        DateTime now)
        => roots
            .SelectMany(root => GetExecutionUnit(task, root))
            .Where(iface => !InterfaceAccessWindow.IsOpen(iface, now))
            .Select(iface => InterfaceAccessWindow.GetNextOpen(iface, now))
            .DefaultIfEmpty(now)
            .Min();

    private static List<SyncTaskInterface> GetExecutionUnit(SyncTask task, SyncTaskInterface root)
    {
        var result = new List<SyncTaskInterface> { root };
        var parentKeys = new Queue<string>();
        if (!string.IsNullOrWhiteSpace(root.InterfaceKey))
            parentKeys.Enqueue(root.InterfaceKey);

        while (parentKeys.Count > 0)
        {
            var parentKey = parentKeys.Dequeue();
            foreach (var child in task.Interfaces.Where(iface =>
                         iface.Enabled && string.Equals(
                             iface.ParentInterfaceKey,
                             parentKey,
                             StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(child);
                if (!string.IsNullOrWhiteSpace(child.InterfaceKey))
                    parentKeys.Enqueue(child.InterfaceKey);
            }
        }

        return result;
    }

    /// <summary>
    /// 每天执行一次日志清理
    /// </summary>
    private async Task CleanupLogsIfDueAsync(CancellationToken ct)
    {
        if ((DateTime.Now - _lastCleanupTime).TotalHours < 24)
            return;

        var retentionDays = _configuration.GetValue("Sync:LogRetentionDays", 30);
        if (retentionDays <= 0)
            return;

        using var scope = _scopeFactory.CreateScope();
        var logService = scope.ServiceProvider.GetRequiredService<SyncLogService>();
        var deleted = await logService.CleanupLogsAsync(retentionDays, ct);

        if (deleted > 0)
            _logger.LogInformation("日志清理完成：删除 {Count} 条（保留 {Days} 天）", deleted, retentionDays);

        _lastCleanupTime = DateTime.Now;
    }
}
