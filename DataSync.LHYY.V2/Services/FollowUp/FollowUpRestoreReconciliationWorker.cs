namespace DataSync.LHYY.V2.Services.FollowUp;

internal enum FollowUpRestoreReconciliationResult
{
    CompletedCurrent,
    CompletedAuditOnly,
    AlreadyCompleted,
    AlreadyTerminal,
    FailedFromMarker,
    CompletedFromAudit,
    SupersededInterrupted,
    PendingCurrent,
    Conflict
}

internal interface IFollowUpRestoreCompletionReconciler
{
    Task<FollowUpRestoreReconciliationResult> ReconcileAsync(
        FollowUpRestoreCompletionMarker marker,
        CancellationToken cancellationToken);
}

internal sealed class FollowUpRestoreReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    FollowUpRestoreCompletionStore completionStore,
    ILogger<FollowUpRestoreReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FollowUp 恢复完成状态后台补写暂时失败，将继续重试。");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        foreach (var marker in await completionStore.ReadAllAsync(cancellationToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var reconciler = scope.ServiceProvider.GetRequiredService<IFollowUpRestoreCompletionReconciler>();
                var result = await reconciler.ReconcileAsync(marker, cancellationToken);
                if (result == FollowUpRestoreReconciliationResult.PendingCurrent)
                {
                    logger.LogDebug(
                        "FollowUp 当前恢复标记仍在执行或结果未知，继续保留。PackageId={PackageId}, RestoreId={RestoreId}",
                        marker.PackageId, marker.RestoreId);
                    continue;
                }
                if (result == FollowUpRestoreReconciliationResult.Conflict)
                {
                    logger.LogCritical(
                        "FollowUp 恢复完成补写标记与恢复记录不一致，保留标记并等待人工检查。PackageId={PackageId}, RestoreId={RestoreId}",
                        marker.PackageId, marker.RestoreId);
                    continue;
                }

                await completionStore.DeleteAsync(marker.RestoreId, cancellationToken);
                logger.LogInformation(
                    "FollowUp 恢复完成状态后台补写结束。PackageId={PackageId}, RestoreId={RestoreId}, Result={Result}",
                    marker.PackageId, marker.RestoreId, result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "FollowUp 单个恢复完成标记补写失败，将保留该标记并继续处理后续标记。PackageId={PackageId}, RestoreId={RestoreId}",
                    marker.PackageId, marker.RestoreId);
            }
        }
    }
}
