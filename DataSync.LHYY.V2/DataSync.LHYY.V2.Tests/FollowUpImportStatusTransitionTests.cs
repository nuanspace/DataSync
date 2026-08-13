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
    [InlineData(false, null, "ImportFailed")]
    [InlineData(true, null, "RestoreFailed")]
    [InlineData(false, "SCHEMA_REVIEW_REQUIRED", "WaitingForDecision")]
    [InlineData(true, "SCHEMA_REVIEW_REQUIRED", "RestoreFailed")]
    public void 导入失败按异常性质和附件回滚结果进入对应状态(
        bool attachmentRestoreFailed,
        string? errorCode,
        string expectedStatus)
    {
        Exception exception = errorCode is null
            ? new InvalidOperationException("导入失败")
            : new DataSync.Common.FollowUp.FollowUpPackageException(errorCode, "需要结构处理");
        var result = FollowUpPackageImportService.ResolveFailureStatus(exception, attachmentRestoreFailed);

        Assert.Equal(expectedStatus, result);
    }
}
