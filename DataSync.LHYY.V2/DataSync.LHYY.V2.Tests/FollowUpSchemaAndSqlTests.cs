using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Services.FollowUp;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class FollowUpSchemaAndSqlTests
{
    [Fact]
    public void 目标结构完全兼容时允许导入()
    {
        var source = CreateTable("uuid", false);
        var target = CreateTable("uuid", false);

        var result = FollowUpPackageSchemaCheckService.Evaluate([source], [target], []);

        Assert.True(result.Compatible);
        Assert.Equal("Compatible", result.DiffLevel);
    }

    [Fact]
    public void 目标表缺失时要求人工映射()
    {
        var result = FollowUpPackageSchemaCheckService.Evaluate([CreateTable("uuid", false)], [], []);

        Assert.False(result.Compatible);
        Assert.Equal("RequiresMapping", result.DiffLevel);
    }

    [Fact]
    public void 同名字段类型不兼容时判定Breaking()
    {
        var result = FollowUpPackageSchemaCheckService.Evaluate(
            [CreateTable("uuid", false)], [CreateTable("integer", false)], []);

        Assert.False(result.Compatible);
        Assert.Equal("Breaking", result.DiffLevel);
    }

    [Fact]
    public void Upsert语句只更新非主键字段且不会生成Delete()
    {
        var sql = FollowUpUpsertSqlBuilder.Build("care", "patient", ["id", "name"], ["id"], "Upsert");

        Assert.Contains("ON CONFLICT (\"id\") DO UPDATE", sql);
        Assert.Contains("\"name\" = EXCLUDED.\"name\"", sql);
        Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 非法标识符被拒绝()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FollowUpUpsertSqlBuilder.Build("care;drop", "patient", ["id"], ["id"], "Upsert"));
    }

    private static FollowUpTableSchema CreateTable(string idType, bool nullable) => new()
    {
        SchemaName = "care",
        TableName = "patient",
        PrimaryKey = ["id"],
        Columns =
        [
            new FollowUpColumnSchema { Name = "id", DataType = idType, IsNullable = nullable, OrdinalPosition = 1 },
            new FollowUpColumnSchema { Name = "name", DataType = "text", IsNullable = true, OrdinalPosition = 2 }
        ]
    };
}
