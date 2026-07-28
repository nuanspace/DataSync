using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using DataSync.LHYY.V2.Services.FollowUp;
using System.Text.Json;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class FollowUpSchemaDecisionProcessorTests
{
    [Fact]
    public void 映射决定会转换目标表字段主键和默认值()
    {
        var decision = new FollowUpSchemaDecision
        {
            DecisionStatus = "ApprovedMapping",
            TableMappings =
            {
                ["public.source_patient"] = new FollowUpTableMapping
                {
                    TargetSchema = "ntcare",
                    TargetTable = "patient",
                    ColumnMappings = { ["source_id"] = "id", ["source_name"] = "name" },
                    DefaultValues = { ["tenant_id"] = JsonSerializer.SerializeToElement(7) }
                }
            }
        };
        var source = new FollowUpTableSchema
        {
            SchemaName = "public",
            TableName = "source_patient",
            PrimaryKey = ["source_id"],
            Columns =
            [
                new() { Name = "source_id", DataType = "uuid" },
                new() { Name = "source_name", DataType = "text" }
            ]
        };

        var mapped = FollowUpSchemaDecisionProcessor.MapSchema(source, decision);
        var row = FollowUpSchemaDecisionProcessor.MapRow(
            "{\"source_id\":\"42\",\"source_name\":\"测试\"}",
            "public",
            "source_patient",
            decision);

        Assert.Equal("ntcare", mapped.SchemaName);
        Assert.Equal("patient", mapped.TableName);
        Assert.Equal(["id"], mapped.PrimaryKey);
        Assert.Equal(["id", "name"], mapped.Columns.Select(item => item.Name));
        using var document = JsonDocument.Parse(row);
        Assert.Equal("42", document.RootElement.GetProperty("id").GetString());
        Assert.Equal(7, document.RootElement.GetProperty("tenant_id").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("source_id", out _));
    }

    [Fact]
    public void 动态行映射前先移除范围外字段并保留批准默认值()
    {
        var decision = new FollowUpSchemaDecision
        {
            DecisionStatus = "ApprovedMapping",
            TableMappings =
            {
                ["target.source_answers"] = new FollowUpTableMapping
                {
                    TargetSchema = "target",
                    TargetTable = "hospital_answers",
                    ColumnMappings = { ["active_question"] = "hospital_question" },
                    DefaultValues = { ["tenant_id"] = JsonSerializer.SerializeToElement(7) }
                }
            }
        };

        var row = FollowUpSchemaDecisionProcessor.MapRow(
            "{\"id\":\"42\",\"active_question\":\"保留\",\"historical_question\":\"敏感值\"}",
            "target",
            "source_answers",
            decision,
            new HashSet<string>(["id", "active_question"], StringComparer.OrdinalIgnoreCase));

        using var document = JsonDocument.Parse(row);
        Assert.Equal("42", document.RootElement.GetProperty("id").GetString());
        Assert.Equal("保留", document.RootElement.GetProperty("hospital_question").GetString());
        Assert.Equal(7, document.RootElement.GetProperty("tenant_id").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("historical_question", out _));
        Assert.DoesNotContain("敏感值", row, StringComparison.Ordinal);
    }

    [Fact]
    public void 无字段映射时仍按动态范围移除未关联非空字段()
    {
        var row = FollowUpSchemaDecisionProcessor.MapRow(
            "{\"id\":\"42\",\"active_question\":\"保留\",\"historical_question\":\"敏感值\"}",
            "target",
            "source_answers",
            decision: null,
            new HashSet<string>(["id", "active_question"], StringComparer.OrdinalIgnoreCase));

        using var document = JsonDocument.Parse(row);
        Assert.Equal("42", document.RootElement.GetProperty("id").GetString());
        Assert.Equal("保留", document.RootElement.GetProperty("active_question").GetString());
        Assert.False(document.RootElement.TryGetProperty("historical_question", out _));
        Assert.DoesNotContain("敏感值", row, StringComparison.Ordinal);
    }
}
