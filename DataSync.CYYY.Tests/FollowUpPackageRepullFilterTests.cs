using DataSync.Common.FollowUp;
using DataSync.CYYY.Models.FollowUp;
using DataSync.CYYY.Services.FollowUp;
using Xunit;

namespace DataSync.CYYY.Tests;

public sealed class FollowUpPackageRepullFilterTests
{
    [Fact]
    public void 按包号筛选时只返回目标包()
    {
        var packages = CreatePackages();

        var result = FollowUpPackageSyncService.FilterRepullCandidates(packages, "pkg-2", null, null);

        Assert.Collection(result, item => Assert.Equal("pkg-2", item.PackageId));
    }

    [Fact]
    public void 按时间范围筛选时返回水位区间相交的包()
    {
        var packages = CreatePackages();

        var result = FollowUpPackageSyncService.FilterRepullCandidates(
            packages,
            null,
            new DateTime(2026, 7, 2, 12, 0, 0),
            new DateTime(2026, 7, 3, 12, 0, 0));

        Assert.Equal(["pkg-2", "pkg-3"], result.Select(item => item.PackageId));
    }

    [Fact]
    public void 同步概览只显示当前医院的数据包和回执()
    {
        var overview = new FollowUpPackageSyncOverview
        {
            Packages =
            [
                new() { HospitalCode = "A", PackageId = "pkg-a" },
                new() { HospitalCode = "B", PackageId = "pkg-b" }
            ],
            Acks =
            [
                new() { HospitalCode = "A", PackageId = "pkg-a" },
                new() { HospitalCode = "B", PackageId = "pkg-b" }
            ]
        };

        Assert.Collection(overview.PackagesFor("B"), item => Assert.Equal("pkg-b", item.PackageId));
        Assert.Collection(overview.AcksFor("B"), item => Assert.Equal("pkg-b", item.PackageId));
        Assert.Empty(overview.PackagesFor(null));
        Assert.Empty(overview.AcksFor(null));
    }

    [Fact]
    public void 普通同步会合并低序号失败包并按序号重试()
    {
        var remote = new[]
        {
            new FollowUpPackageSummary { PackageId = "pkg-11", SequenceNo = 11 }
        };
        var failed = new[]
        {
            new FollowUpPackageSummary { PackageId = "pkg-5", SequenceNo = 5 }
        };

        var result = FollowUpPackageSyncService.MergePullCandidates(remote, failed);

        Assert.Equal(["pkg-5", "pkg-11"], result.Select(item => item.PackageId));
    }

    [Fact]
    public void 远端清单与失败队列包含同一包时只拉取一次()
    {
        var remote = new[]
        {
            new FollowUpPackageSummary { PackageId = "pkg-5", SequenceNo = 5 }
        };
        var failed = new[]
        {
            new FollowUpPackageSummary { PackageId = "pkg-5", SequenceNo = 5 }
        };

        var result = FollowUpPackageSyncService.MergePullCandidates(remote, failed);

        Assert.Collection(result, item => Assert.Equal("pkg-5", item.PackageId));
    }

    [Theory]
    [InlineData("Pending", true)]
    [InlineData("Failed", true)]
    [InlineData("Pulling", true)]
    [InlineData("Pulled", false)]
    public void 进程中断遗留的拉取中状态可重新领取(string status, bool expected)
    {
        Assert.Equal(expected, FollowUpPackageRetryPolicy.IsRetryable(status));
    }

    private static List<FollowUpPackageSummary> CreatePackages() =>
    [
        new() { PackageId = "pkg-1", SequenceNo = 1, FromWatermark = new DateTime(2026, 7, 1), ToWatermark = new DateTime(2026, 7, 2) },
        new() { PackageId = "pkg-2", SequenceNo = 2, FromWatermark = new DateTime(2026, 7, 2), ToWatermark = new DateTime(2026, 7, 3) },
        new() { PackageId = "pkg-3", SequenceNo = 3, FromWatermark = new DateTime(2026, 7, 3), ToWatermark = new DateTime(2026, 7, 4) }
    ];
}
