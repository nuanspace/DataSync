using DataSync.LHYY.V2.Services.FollowUp;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class FollowUpImportStatusTransitionTests
{
    [Theory]
    [InlineData("RejectedSchemaMismatch")]
    [InlineData("WaitingForDecision")]
    [InlineData("Restored")]
    [InlineData("RestoreFailed")]
    [InlineData("ImportFailed")]
    public void 发现已拉取包时保留终态和人工处理状态(string currentStatus)
    {
        var result = FollowUpPackageImportRepository.ResolveDiscoveryStatus(currentStatus, "Pulled");

        Assert.Equal(currentStatus, result);
    }

    [Theory]
    [InlineData(null, "Pending")]
    [InlineData("AwaitingPackage", "Pending")]
    [InlineData("WaitingForPredecessor", "Pending")]
    public void 新包到达或前驱可能就绪时进入待处理(string? currentStatus, string expectedStatus)
    {
        var result = FollowUpPackageImportRepository.ResolveDiscoveryStatus(currentStatus, "Pulled");

        Assert.Equal(expectedStatus, result);
    }

    [Fact]
    public void 包尚未拉取时新记录保持等待文件()
    {
        var result = FollowUpPackageImportRepository.ResolveDiscoveryStatus(null, "Failed");

        Assert.Equal("AwaitingPackage", result);
    }

    [Theory]
    [InlineData(false, "ImportFailed")]
    [InlineData(true, "RestoreFailed")]
    public void 附件回滚失败时进入恢复失败状态(bool attachmentRestoreFailed, string expectedStatus)
    {
        var result = FollowUpPackageImportService.ResolveFailureStatus(attachmentRestoreFailed);

        Assert.Equal(expectedStatus, result);
    }
}
