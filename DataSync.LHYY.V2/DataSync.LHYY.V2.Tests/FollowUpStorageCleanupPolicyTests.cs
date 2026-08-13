using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class FollowUpStorageCleanupPolicyTests
{
    [Fact]
    public void 清理仓储同时要求恢复基线_成功ACK_hash和转发状态()
    {
        var source = ReadSource("Services", "FollowUp", "FollowUpPackageImportRepository.cs");

        Assert.Contains("state.package_type = 'Baseline'", source, StringComparison.Ordinal);
        Assert.Contains("pull_state.trigger_type = 'RecoveryBaseline'", source, StringComparison.Ordinal);
        Assert.Contains("state.sequence_no < @recoverySequence", source, StringComparison.Ordinal);
        Assert.Contains("ack.ack_status IN ('Imported', 'Succeeded')", source, StringComparison.Ordinal);
        Assert.Contains("ack.forward_status = 'Forwarded'", source, StringComparison.Ordinal);
        Assert.Contains("lower(ack.ack_payload_json->>'receivedHash') = lower(state.package_hash)", source, StringComparison.Ordinal);
        Assert.Contains("pull_status = 'Archived'", source, StringComparison.Ordinal);
        Assert.Contains("backup_status = 'Archived'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void 文件服务校验受控路径_hash和附件清单()
    {
        var source = ReadSource("Services", "FollowUp", "FollowUpHospitalStorageService.cs");
        var backupSource = ReadSource("Services", "FollowUp", "FollowUpPackageBackupService.cs");

        Assert.Contains("ValidateManagedFile", source, StringComparison.Ordinal);
        Assert.Contains("HashFileAsync", source, StringComparison.Ordinal);
        Assert.Contains("ValidateRegisteredAttachmentBackupAsync", source, StringComparison.Ordinal);
        Assert.Contains("AttachmentManifestHash", source, StringComparison.Ordinal);
        Assert.Contains("attachment-backup.json", backupSource, StringComparison.Ordinal);
        Assert.Contains("TryAcquireExclusiveAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void 旧备份清理必须复用完整登记校验和历史人工门控()
    {
        var source = ReadSource("Services", "FollowUp", "FollowUpHospitalStorageService.cs");
        var methodStart = source.IndexOf("private async Task ValidateBackupAtRootAsync(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("private static", methodStart, StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var methodSource = source[methodStart..methodEnd];

        Assert.Contains("ValidateRegisteredAttachmentBackupAsync", methodSource, StringComparison.Ordinal);
        Assert.Contains("backup.SizeBytes", methodSource, StringComparison.Ordinal);
        Assert.Contains("backup.AttachmentManifestHash", methodSource, StringComparison.Ordinal);
        Assert.Contains("backup.AttachmentEntryCount", methodSource, StringComparison.Ordinal);
    }

    [Fact]
    public void 旧包清理必须先原子隔离再校验冻结对象且最后归档数据库()
    {
        var source = ReadSource("Services", "FollowUp", "FollowUpHospitalStorageService.cs");
        var methodStart = source.IndexOf("public async Task<FollowUpImportOperationResult> CleanupAsync(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("public async Task ReconcilePendingAsync(", methodStart, StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var methodSource = source[methodStart..methodEnd];

        var move = methodSource.IndexOf("MoveToQuarantine", StringComparison.Ordinal);
        var validateFrozen = methodSource.IndexOf("ValidateQuarantinedCandidateAsync", StringComparison.Ordinal);
        var archive = methodSource.IndexOf("CompleteStorageCleanupAsync", StringComparison.Ordinal);
        Assert.True(move >= 0, "清理必须先把精确对象原子隔离到 quarantine。");
        Assert.True(validateFrozen > move, "完整登记校验必须针对已隔离的冻结对象执行。");
        Assert.True(archive > validateFrozen, "只有隔离对象完整校验通过后才能归档数据库状态。");
    }

    [Fact]
    public void 中断清理恢复和删除必须复验隔离路径且恢复后复验规范原路径()
    {
        var source = ReadSource("Services", "FollowUp", "FollowUpHospitalStorageService.cs");
        var validateStart = source.IndexOf("private void ValidateManifestStructure(", StringComparison.Ordinal);
        var validateEnd = source.IndexOf("private async Task ValidateQuarantinedCandidateAsync(", validateStart, StringComparison.Ordinal);
        Assert.True(validateStart >= 0 && validateEnd > validateStart);
        var validateSource = source[validateStart..validateEnd];
        Assert.Contains("ValidateQuarantinePath", validateSource, StringComparison.Ordinal);

        var cleanupStart = source.IndexOf("public async Task<FollowUpImportOperationResult> CleanupAsync(", StringComparison.Ordinal);
        var cleanupEnd = source.IndexOf("public async Task ReconcilePendingAsync(", cleanupStart, StringComparison.Ordinal);
        var cleanupSource = source[cleanupStart..cleanupEnd];
        var archived = cleanupSource.IndexOf("FollowUpStorageCleanupPhase.DatabaseArchived", StringComparison.Ordinal);
        var cleanupRevalidation = cleanupSource.IndexOf("ValidateManifestStructure(manifest)", archived, StringComparison.Ordinal);
        var cleanupDelete = cleanupSource.IndexOf("DeleteQuarantine", archived, StringComparison.Ordinal);
        Assert.True(archived >= 0 && cleanupRevalidation > archived && cleanupDelete > cleanupRevalidation,
            "数据库归档后删除隔离对象前必须重新校验隔离路径。");

        var reconcileStart = source.IndexOf("private async Task<bool> ReconcileOneAsync(", StringComparison.Ordinal);
        var reconcileEnd = source.IndexOf("private void ValidateManifestStructure(", reconcileStart, StringComparison.Ordinal);
        var reconcileSource = source[reconcileStart..reconcileEnd];
        var restore = reconcileSource.IndexOf("FollowUpStorageCleanupFileRecovery.Restore", StringComparison.Ordinal);
        var postRestoreValidation = reconcileSource.IndexOf("ValidateManifestStructure(manifest)", restore, StringComparison.Ordinal);
        var restoredContentValidation = reconcileSource.IndexOf("ValidateRestoredCandidateAsync", restore, StringComparison.Ordinal);
        var cancelDatabase = reconcileSource.IndexOf("CancelStorageCleanupAsync", restore, StringComparison.Ordinal);
        Assert.True(restore >= 0
                    && postRestoreValidation > restore
                    && restoredContentValidation > postRestoreValidation
                    && cancelDatabase > restoredContentValidation,
            "隔离对象恢复后必须先确认规范原路径不是链接并复验全部登记内容，才能取消数据库准备态并删除清单。");
    }

    private static string ReadSource(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DataSync.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, "DataSync.LHYY.V2", .. relativePath]));
    }
}
