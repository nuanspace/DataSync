using DataSync.Common.FollowUp;

namespace DataSync.CYYY.Tests;

public sealed class FollowUpDisplayTextTests
{
    [Theory]
    [InlineData("Baseline", "基础包")]
    [InlineData("Incremental", "增量包")]
    [InlineData("Supplement", "补充包")]
    [InlineData("Replacement", "替代包")]
    public void 包类型显示为中文(string value, string expected)
    {
        Assert.Equal(expected, FollowUpDisplayText.PackageType(value));
    }

    [Theory]
    [InlineData("Compatible", "兼容")]
    [InlineData("Additive", "仅新增")]
    [InlineData("Breaking", "不兼容")]
    [InlineData(null, "未检查")]
    public void 结构差异显示为中文(string? value, string expected)
    {
        Assert.Equal(expected, FollowUpDisplayText.SchemaDiff(value));
    }

    [Fact]
    public void 包状态和回执状态显示为中文()
    {
        Assert.Equal("已拉取", FollowUpDisplayText.PullStatus("Pulled"));
        Assert.Equal("已导入", FollowUpDisplayText.ImportStatus("Imported"));
        Assert.Equal("导入失败", FollowUpDisplayText.AckStatus("ImportFailed"));
        Assert.Equal("已转发", FollowUpDisplayText.ForwardStatus("Forwarded"));
        Assert.Equal("定时生成", FollowUpDisplayText.TriggerType("Scheduled"));
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("WaitingForPredecessor")]
    [InlineData("WaitingForDecision")]
    [InlineData("RejectedSchemaMismatch")]
    [InlineData("ImportFailed")]
    [InlineData("Restored")]
    public void 可处理状态允许重新校验导入(string status)
    {
        Assert.True(FollowUpDisplayText.CanImport(status));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AwaitingPackage")]
    [InlineData("Validating")]
    [InlineData("BackingUp")]
    [InlineData("Importing")]
    [InlineData("Restoring")]
    [InlineData("RestoreFailed")]
    [InlineData("Imported")]
    [InlineData("Unknown")]
    public void 不安全状态禁止直接导入(string? status)
    {
        Assert.False(FollowUpDisplayText.CanImport(status));
    }

    [Theory]
    [InlineData("Imported")]
    [InlineData("ImportFailed")]
    [InlineData("RestoreRequired")]
    [InlineData("RestoreFailed")]
    public void 可恢复状态显示恢复入口(string status)
    {
        Assert.True(FollowUpDisplayText.CanRestore(status));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Pending")]
    [InlineData("Restoring")]
    [InlineData("Restored")]
    public void 非恢复状态不显示恢复入口(string? status)
    {
        Assert.False(FollowUpDisplayText.CanRestore(status));
    }

    [Fact]
    public void 拉取间隔按分钟编辑并说明滚动调度规则()
    {
        Assert.Equal(5, FollowUpDisplayText.PullIntervalMinutes(300));
        Assert.Equal(300, FollowUpDisplayText.PullIntervalSeconds(5));
        Assert.Equal("服务每 30 秒扫描一次；达到 5 分钟滚动间隔后执行，实际时间受扫描周期及其他拉取任务耗时影响。",
            FollowUpDisplayText.PullScheduleDescription(300));
    }

    [Theory]
    [InlineData(true, true, "定时拉取已启用")]
    [InlineData(true, false, "仅手工拉取")]
    [InlineData(false, true, "计划已保存，自动拉取服务未启用")]
    [InlineData(false, false, "仅手工拉取")]
    public void 来源调度状态同时反映全局服务和来源计划(
        bool globalEnabled,
        bool sourceEnabled,
        string expected)
    {
        Assert.Equal(expected, FollowUpDisplayText.SourceScheduleStatus(globalEnabled, sourceEnabled));
    }
}
