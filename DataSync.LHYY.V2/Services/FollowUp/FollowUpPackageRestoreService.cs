using DataSync.LHYY.V2.Models.FollowUp;
using DataSync.Common.FollowUp;
using Microsoft.Extensions.Options;

namespace DataSync.LHYY.V2.Services.FollowUp;

public sealed class FollowUpPackageRestoreService(
    FollowUpPackageImportRepository repository,
    FollowUpPackageBackupService backupService,
    FollowUpCubeOperationCoordinator operationCoordinator,
    FollowUpRestoreCompletionStore completionStore,
    IOptions<FollowUpPackageImportOptions> options,
    ILogger<FollowUpPackageRestoreService> logger)
{
    internal static string ResolveTerminalStatus(bool restoreCompleted) =>
        restoreCompleted ? "Restored" : "RestoreFailed";

    internal static bool CanDeleteRestoreMarker(bool stateWritten, bool auditWritten, bool logWritten) =>
        stateWritten && auditWritten && logWritten;

    internal static bool ShouldWriteTerminalState(bool restoreStateEntered, bool restoreCompleted) =>
        restoreStateEntered || restoreCompleted;

    internal static bool IsRestoreCompletedException(Exception exception) =>
        exception is FollowUpRestoreCleanupException
        || exception is AggregateException aggregate
        && aggregate.InnerExceptions.Any(IsRestoreCompletedException);

    internal static FollowUpImportOperationResult? ResolveCompletedMarkerPreflightResult(
        IReadOnlyCollection<FollowUpRestoreReconciliationResult> results)
    {
        if (results.Contains(FollowUpRestoreReconciliationResult.CompletedCurrent))
            return new FollowUpImportOperationResult(true, "数据库和附件恢复已完成，管理状态已补写，无需重复恢复。");
        if (results.Contains(FollowUpRestoreReconciliationResult.Conflict))
            return new FollowUpImportOperationResult(
                false,
                "检测到已完成恢复标记与管理记录不一致，请检查 DataSync 日志并人工处理，禁止重复恢复。",
                FollowUpErrorCodes.InternalError);
        return null;
    }

    public async Task<FollowUpImportOperationResult> RestoreAsync(FollowUpPackageImportState state, CancellationToken cancellationToken = default)
    {
        Guid? restoreId = null;
        var restoreStateEntered = false;
        var restoreCompleted = false;
        DateTimeOffset? restoredAt = null;
        FollowUpRestoreCompletionMarker? reconciliationMarker = null;
        try
        {
            if (!FollowUpDisplayText.CanRestore(state.ImportStatus))
                return new FollowUpImportOperationResult(false, $"当前状态不允许恢复：{state.ImportStatus}。");
            await using var operationLease = await operationCoordinator.TryAcquireRecoveryExclusiveAsync(cancellationToken);
            if (operationLease is null)
                return new FollowUpImportOperationResult(false, "CubeDb 当前有写入或维护任务正在执行。");
            var completedMarkerResult = await ReconcileCompletedMarkersAsync(state, cancellationToken);
            if (completedMarkerResult is not null) return completedMarkerResult;
            var authoritativeStatus = await repository.GetPackageStatusAsync(state.HospitalCode, state.PackageId, cancellationToken);
            if (!FollowUpDisplayText.CanRestore(authoritativeStatus.Status))
                return new FollowUpImportOperationResult(false, $"数据库中的当前状态不允许恢复：{authoritativeStatus.Status ?? "不存在"}。");
            var currentHead = await repository.GetCurrentRestorableHeadAsync(state.HospitalCode, cancellationToken);
            if (!string.Equals(currentHead, state.PackageId, StringComparison.Ordinal))
                throw new InvalidOperationException("只能恢复当前已导入链头；如需回退多个包，请按实际完成顺序倒序逐包恢复。");
            var backup = await repository.GetLatestBackupAsync(state.HospitalCode, state.PackageId, cancellationToken)
                ?? throw new InvalidOperationException("该包没有可用备份，不能恢复。");
            restoreId = await repository.StartRestoreAsync(state, backup, options.Value.DeviceId, cancellationToken);
            reconciliationMarker = new FollowUpRestoreCompletionMarker(
                restoreId.Value, state.HospitalCode, state.PackageId, backup.RecordId, null, null);
            await completionStore.SaveAsync(reconciliationMarker, cancellationToken);
            await repository.MarkAsync(state.HospitalCode, state.PackageId, "Restoring", null, null, cancellationToken: cancellationToken);
            restoreStateEntered = true;
            await backupService.RestoreAsync(backup, cancellationToken);
            restoreCompleted = true;
            restoredAt = DateTimeOffset.Now;
            reconciliationMarker = reconciliationMarker with { RestoredAt = restoredAt };
            await completionStore.SaveAsync(reconciliationMarker, CancellationToken.None);
            await repository.CompleteRestoreAsync(
                state.HospitalCode,
                state.PackageId,
                null,
                new { backup.RecordId, restoredAt },
                cancellationToken);
            await repository.FinishRestoreAsync(restoreId.Value, "Completed", new { backup.RecordId }, null, null, cancellationToken);
            await repository.AddRestoreCompletionLogAsync(reconciliationMarker, cancellationToken);
            await completionStore.DeleteAsync(restoreId.Value, CancellationToken.None);
            return new FollowUpImportOperationResult(true, "数据库和附件恢复完成，可重新导入该包。");
        }
        catch (Exception ex)
        {
            var restoreCleanupFailed = IsRestoreCompletedException(ex);
            if (!restoreCompleted && restoreCleanupFailed)
            {
                restoreCompleted = true;
                restoredAt = DateTimeOffset.Now;
                if (reconciliationMarker is not null)
                    reconciliationMarker = reconciliationMarker with { RestoredAt = restoredAt };
            }
            if (restoreCompleted)
                logger.LogError(ex,
                    restoreCleanupFailed
                        ? "FollowUp 数据库和附件已恢复，但临时快照清理失败。PackageId={PackageId}"
                        : "FollowUp 数据库和附件已恢复，审计记录写入失败。PackageId={PackageId}",
                    state.PackageId);
            else
                logger.LogError(ex, "FollowUp 包恢复失败。PackageId={PackageId}", state.PackageId);
            if (restoreId.HasValue)
            {
                var terminalStatus = ResolveTerminalStatus(restoreCompleted);
                if (reconciliationMarker is not null)
                {
                    try
                    {
                        reconciliationMarker = restoreCompleted
                            ? reconciliationMarker with { AuditError = ex.Message }
                            : reconciliationMarker with { RestoreError = ex.Message };
                        await completionStore.SaveAsync(reconciliationMarker, CancellationToken.None);
                    }
                    catch (Exception markerException)
                    {
                        logger.LogCritical(markerException,
                            "FollowUp 恢复完成补写标记更新失败。PackageId={PackageId}", state.PackageId);
                    }
                }
                var shouldWriteTerminalState = ShouldWriteTerminalState(restoreStateEntered, restoreCompleted);
                var stateWritten = !shouldWriteTerminalState;
                var auditWritten = false;
                var logWritten = !restoreCompleted;
                if (shouldWriteTerminalState)
                {
                    try
                    {
                        if (restoreCompleted)
                        {
                            var restoreMessage = restoreCleanupFailed
                                ? "数据库和附件已恢复，但临时快照清理失败，必须人工清理残留；不得重复恢复。"
                                : "数据库和附件已恢复，审计记录补写失败。";
                            var restoreSummary = new
                            {
                                restoredAt,
                                cleanupError = restoreCleanupFailed ? ex.Message : null,
                                auditError = restoreCleanupFailed ? null : ex.Message
                            };
                            await repository.CompleteRestoreAsync(
                                state.HospitalCode,
                                state.PackageId,
                                restoreMessage,
                                restoreSummary,
                                CancellationToken.None);
                        }
                        else
                        {
                            await repository.MarkAsync(
                                state.HospitalCode,
                                state.PackageId,
                                terminalStatus,
                                FollowUpErrorCodes.InternalError,
                                ex.Message,
                                cancellationToken: CancellationToken.None);
                        }
                        stateWritten = true;
                    }
                    catch (Exception stateException)
                    {
                        logger.LogCritical(stateException,
                            restoreCompleted
                                ? "FollowUp 恢复成功状态补写失败，原 Restoring 状态将继续阻断写入。PackageId={PackageId}"
                                : "FollowUp 恢复失败状态写入失败，原 Restoring 状态将继续阻断写入。PackageId={PackageId}",
                            state.PackageId);
                    }
                }
                try
                {
                    await repository.FinishRestoreAsync(
                        restoreId.Value,
                        restoreCompleted ? "Completed" : "Failed",
                        restoreCompleted
                            ? restoreCleanupFailed
                                ? new { restoredAt, cleanupError = ex.Message }
                                : new { restoredAt, auditError = ex.Message }
                            : null,
                        restoreCompleted ? null : FollowUpErrorCodes.InternalError,
                        restoreCompleted ? null : ex.Message,
                        CancellationToken.None);
                    auditWritten = true;
                }
                catch (Exception auditException)
                {
                    logger.LogError(auditException,
                        restoreCompleted
                            ? "FollowUp 恢复成功审计记录补写失败。PackageId={PackageId}"
                            : "FollowUp 恢复失败审计记录补写失败。PackageId={PackageId}",
                        state.PackageId);
                }
                if (restoreCompleted && reconciliationMarker is not null)
                {
                    try
                    {
                        await repository.AddRestoreCompletionLogAsync(reconciliationMarker, CancellationToken.None);
                        logWritten = true;
                    }
                    catch (Exception logException)
                    {
                        logger.LogError(logException,
                            "FollowUp 恢复成功日志补写失败。PackageId={PackageId}", state.PackageId);
                    }
                }
                if (CanDeleteRestoreMarker(stateWritten, auditWritten, logWritten))
                {
                    try
                    {
                        await completionStore.DeleteAsync(restoreId.Value, CancellationToken.None);
                    }
                    catch (Exception markerException)
                    {
                        logger.LogWarning(markerException,
                            "FollowUp 恢复标记删除失败，将由后台幂等处理或保留供人工检查。PackageId={PackageId}", state.PackageId);
                    }
                }
            }
            if (restoreCompleted)
                return new FollowUpImportOperationResult(true,
                    restoreCleanupFailed
                        ? "数据库和附件恢复已完成；临时快照清理异常，请人工清理残留并检查 DataSync 日志，无需重复恢复。"
                        : "数据库和附件恢复已完成；审计记录写入异常，请检查 DataSync 日志，无需重复恢复。",
                    FollowUpErrorCodes.InternalError);
            return new FollowUpImportOperationResult(false, ex.Message, FollowUpErrorCodes.InternalError);
        }
    }

    private async Task<FollowUpImportOperationResult?> ReconcileCompletedMarkersAsync(
        FollowUpPackageImportState state,
        CancellationToken cancellationToken)
    {
        var results = new List<FollowUpRestoreReconciliationResult>();
        var completionReconciler = (IFollowUpRestoreCompletionReconciler)repository;
        foreach (var marker in await completionStore.ReadCompletedAsync(cancellationToken))
        {
            if (!string.Equals(marker.HospitalCode, state.HospitalCode, StringComparison.Ordinal)
                || !string.Equals(marker.PackageId, state.PackageId, StringComparison.Ordinal))
                continue;

            var result = await completionReconciler.ReconcileAsync(marker, cancellationToken);
            results.Add(result);
            if (result is FollowUpRestoreReconciliationResult.PendingCurrent
                or FollowUpRestoreReconciliationResult.Conflict)
                continue;
            try
            {
                await completionStore.DeleteAsync(marker.RestoreId, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "FollowUp 恢复完成标记删除失败，将由后台继续处理。PackageId={PackageId}, RestoreId={RestoreId}",
                    marker.PackageId, marker.RestoreId);
            }
        }
        return ResolveCompletedMarkerPreflightResult(results);
    }
}
