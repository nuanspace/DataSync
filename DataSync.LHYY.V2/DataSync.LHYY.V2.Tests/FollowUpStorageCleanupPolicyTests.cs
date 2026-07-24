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

        Assert.Contains("ValidateManagedFile", source, StringComparison.Ordinal);
        Assert.Contains("HashFileAsync", source, StringComparison.Ordinal);
        Assert.Contains("attachment-backup.json", source, StringComparison.Ordinal);
        Assert.Contains("TryAcquireExclusiveAsync", source, StringComparison.Ordinal);
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
