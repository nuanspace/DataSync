using DataSync.LHYY.V2.Models.FollowUp;
using DataSync.Common.FollowUp;
using Microsoft.Extensions.Options;

namespace DataSync.LHYY.V2.Services.FollowUp;

public sealed class FollowUpPackageRestoreService(
    FollowUpPackageImportRepository repository,
    FollowUpPackageBackupService backupService,
    FollowUpCubeOperationCoordinator operationCoordinator,
    IOptions<FollowUpPackageImportOptions> options,
    ILogger<FollowUpPackageRestoreService> logger)
{
    public async Task<FollowUpImportOperationResult> RestoreAsync(FollowUpPackageImportState state, CancellationToken cancellationToken = default)
    {
        Guid? restoreId = null;
        try
        {
            if (!FollowUpDisplayText.CanRestore(state.ImportStatus))
                return new FollowUpImportOperationResult(false, $"当前状态不允许恢复：{state.ImportStatus}。");
            await using var operationLease = await operationCoordinator.TryAcquireExclusiveAsync(cancellationToken);
            if (operationLease is null)
                return new FollowUpImportOperationResult(false, "CubeDb 当前有写入或维护任务正在执行。");
            var authoritativeStatus = await repository.GetPackageStatusAsync(state.HospitalCode, state.PackageId, cancellationToken);
            if (!FollowUpDisplayText.CanRestore(authoritativeStatus.Status))
                return new FollowUpImportOperationResult(false, $"数据库中的当前状态不允许恢复：{authoritativeStatus.Status ?? "不存在"}。");
            var currentHead = await repository.GetCurrentRestorableHeadAsync(state.HospitalCode, cancellationToken);
            if (!string.Equals(currentHead, state.PackageId, StringComparison.Ordinal))
                throw new InvalidOperationException("只能恢复当前已导入链头；如需回退多个包，请按序号从大到小逐包恢复。");
            var backup = await repository.GetLatestBackupAsync(state.HospitalCode, state.PackageId, cancellationToken)
                ?? throw new InvalidOperationException("该包没有可用备份，不能恢复。");
            restoreId = await repository.StartRestoreAsync(state, backup, options.Value.DeviceId, cancellationToken);
            await repository.MarkAsync(state.HospitalCode, state.PackageId, "Restoring", null, null, cancellationToken: cancellationToken);
            await backupService.RestoreAsync(backup, cancellationToken);
            await repository.MarkAsync(state.HospitalCode, state.PackageId, "Restored", null, null,
                new { backup.RecordId, restoredAt = DateTimeOffset.Now }, cancellationToken);
            await repository.FinishRestoreAsync(restoreId.Value, "Completed", new { backup.RecordId }, null, null, cancellationToken);
            await repository.AddLogAsync(state.HospitalCode, state.PackageId, "restore", "Info", "数据库和附件已从导入前备份恢复",
                new { backup.RecordId }, cancellationToken);
            return new FollowUpImportOperationResult(true, "数据库和附件恢复完成，可重新导入该包。");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FollowUp 包恢复失败。PackageId={PackageId}", state.PackageId);
            if (restoreId.HasValue)
            {
                await repository.FinishRestoreAsync(restoreId.Value, "Failed", null, FollowUpErrorCodes.InternalError, ex.Message, CancellationToken.None);
                await repository.MarkAsync(state.HospitalCode, state.PackageId, "RestoreFailed", FollowUpErrorCodes.InternalError, ex.Message, cancellationToken: CancellationToken.None);
            }
            return new FollowUpImportOperationResult(false, ex.Message, FollowUpErrorCodes.InternalError);
        }
    }
}
