using DataSync.CYYY.Services;

namespace DataSync.CYYY.Tests;

public class CompositeChildRecordSelectorTests
{
    [Fact]
    public void SelectForMount_同一病历多条记录时选择业务更新时间最新记录()
    {
        var older = CreateRecord("A", "2026-08-07 13:29:22", "2026-08-07T13:29:22+08:00", 1);
        var latest = CreateRecord("B", "2026-08-08 09:00:00", "2026-08-08T09:00:00+08:00", 2);

        var result = CompositeChildRecordSelector.SelectForMount(
            "JHIDS-BAS-FBC-027",
            "FileContents",
            [older, latest]);

        Assert.Single(result);
        Assert.Same(latest, result[0]);
    }

    [Fact]
    public void SelectForMount_业务更新时间缺失时回退系统更新时间()
    {
        var older = CreateRecord("A", null, "2026-08-07T05:29:22.000+0000", 1);
        var latest = CreateRecord("B", null, "2026-08-08T05:29:22.000+0000", 2);

        var result = CompositeChildRecordSelector.SelectForMount(
            "JHIDS-BAS-FBC-027",
            "FileContents",
            [older, latest]);

        Assert.Single(result);
        Assert.Same(latest, result[0]);
    }

    [Fact]
    public void SelectForMount_更新时间相同时按行键降序选择()
    {
        var smallerRowKey = CreateRecord("A", "2026-08-08 09:00:00", null, 10);
        var largerRowKey = CreateRecord("B", "2026-08-08 09:00:00", null, 20);

        var result = CompositeChildRecordSelector.SelectForMount(
            "JHIDS-BAS-FBC-027",
            "FileContents",
            [smallerRowKey, largerRowKey]);

        Assert.Single(result);
        Assert.Same(largerRowKey, result[0]);
    }

    [Fact]
    public void SelectForMount_多条记录均无有效更新时间时拒绝选择()
    {
        var first = CreateRecord("A", null, null, 1);
        var second = CreateRecord("B", "无效时间", "仍然无效", 2);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompositeChildRecordSelector.SelectForMount(
                "JHIDS-BAS-FBC-027",
                "FileContents",
                [first, second]));

        Assert.Contains("无法选择最新记录", exception.Message);
    }

    [Fact]
    public void SelectForMount_其他组合子接口保持原记录集合()
    {
        var first = CreateRecord("A", "2026-08-07 13:29:22", null, 1);
        var second = CreateRecord("B", "2026-08-08 13:29:22", null, 2);
        IReadOnlyList<Dictionary<string, object>> records = new List<Dictionary<string, object>> { first, second };

        var result = CompositeChildRecordSelector.SelectForMount(
            "OTHER-INTERFACE",
            "FileContents",
            records);

        Assert.Same(records, result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SelectForMount_单条记录无需更新时间也正常保留()
    {
        var record = CreateRecord("A", null, null, 1);
        IReadOnlyList<Dictionary<string, object>> records = new List<Dictionary<string, object>> { record };

        var result = CompositeChildRecordSelector.SelectForMount(
            "JHIDS-BAS-FBC-027",
            "FileContents",
            records);

        Assert.Same(records, result);
        Assert.Same(record, result[0]);
    }

    private static Dictionary<string, object> CreateRecord(
        string hisKey,
        string? businessUpdatedAt,
        string? systemUpdatedAt,
        long rowKey)
    {
        var record = new Dictionary<string, object>
        {
            ["HIS_KEY"] = hisKey,
            ["FBC_ROWKEY"] = rowKey,
            ["FILE_CONTENT"] = $"content-{hisKey}"
        };
        if (businessUpdatedAt != null)
            record["PDL_LAST_UPDATE"] = businessUpdatedAt;
        if (systemUpdatedAt != null)
            record["UPDATED_T"] = systemUpdatedAt;

        return record;
    }
}
