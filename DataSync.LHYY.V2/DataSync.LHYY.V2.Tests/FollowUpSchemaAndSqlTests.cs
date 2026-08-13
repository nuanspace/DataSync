using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using DataSync.LHYY.V2.Services.FollowUp;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public void 无导出文件的空表不参与结构校验()
    {
        var result = FollowUpPackageSchemaCheckService.Evaluate(
            [CreateTable("uuid", false)],
            [CreateTable("integer", false)],
            [CreateManifest(exportPath: null, recordCount: 0)]);

        Assert.True(result.Compatible);
        Assert.Equal("Compatible", result.DiffLevel);
    }

    [Fact]
    public void 有导出文件的表仍执行完整结构校验()
    {
        var result = FollowUpPackageSchemaCheckService.Evaluate(
            [CreateTable("uuid", false)],
            [CreateTable("integer", false)],
            [CreateManifest("data/care_patient.jsonl", 1)]);

        Assert.False(result.Compatible);
        Assert.Equal("Breaking", result.DiffLevel);
    }

    [Fact]
    public void 动态表只校验医院关联字段和系统固定字段()
    {
        var source = CreateDynamicTable(
            ("active_question", "text"),
            ("historical_question", "text"));
        var target = CreateDynamicTable(("active_question", "text"));
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");
        var scope = CreateDynamicScope("active_question");

        var scopedSources = FollowUpPackageSchemaCheckService.SelectAndMapSourceTables(
            [source],
            [manifest],
            decision: null,
            [scope]);
        var result = FollowUpPackageSchemaCheckService.Evaluate(
            scopedSources,
            [target],
            [manifest],
            columnScopes: [scope]);

        Assert.True(result.Compatible);
        Assert.DoesNotContain(
            scopedSources.Single().Columns,
            item => item.Name == "historical_question");
    }

    [Fact]
    public void 医院关联字段缺失仍要求人工处理()
    {
        var source = CreateDynamicTable(("active_question", "text"));
        var target = CreateDynamicTable();
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");
        var scope = CreateDynamicScope("active_question");
        var scopedSources = FollowUpPackageSchemaCheckService.SelectAndMapSourceTables(
            [source],
            [manifest],
            decision: null,
            [scope]);

        var result = FollowUpPackageSchemaCheckService.Evaluate(
            scopedSources,
            [target],
            [manifest],
            columnScopes: [scope]);

        Assert.False(result.Compatible);
        Assert.Equal("RequiresMapping", result.DiffLevel);
        Assert.Contains(result.Messages, item => item.Contains("active_question", StringComparison.Ordinal));
    }

    [Fact]
    public void 动态表系统固定字段缺失仍要求人工处理()
    {
        var source = CreateDynamicTable(("active_question", "text"));
        var target = CreateDynamicTable(("active_question", "text"));
        target.Columns.RemoveAll(item => item.Name == "linked_card_sub_id");
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");
        var scope = CreateDynamicScope("active_question");
        var scopedSources = FollowUpPackageSchemaCheckService.SelectAndMapSourceTables(
            [source],
            [manifest],
            decision: null,
            [scope]);

        var result = FollowUpPackageSchemaCheckService.Evaluate(
            scopedSources,
            [target],
            [manifest],
            columnScopes: [scope]);

        Assert.False(result.Compatible);
        Assert.Equal("RequiresMapping", result.DiffLevel);
        Assert.Contains(result.Messages, item => item.Contains("linked_card_sub_id", StringComparison.Ordinal));
    }

    [Fact]
    public void 动态表系统固定字段类型漂移仍判定Breaking()
    {
        var source = CreateDynamicTable();
        var target = CreateDynamicTable();
        source.Columns.Single(item => item.Name == "patient_id").DataType = "uuid";
        target.Columns.Single(item => item.Name == "patient_id").DataType = "text";
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");
        var scope = CreateDynamicScope();
        var scopedSources = FollowUpPackageSchemaCheckService.SelectAndMapSourceTables(
            [source],
            [manifest],
            decision: null,
            [scope]);

        var result = FollowUpPackageSchemaCheckService.Evaluate(
            scopedSources,
            [target],
            [manifest],
            columnScopes: [scope]);

        Assert.False(result.Compatible);
        Assert.Equal("Breaking", result.DiffLevel);
        Assert.Contains(result.Messages, item => item.Contains("patient_id", StringComparison.Ordinal));
    }

    [Fact]
    public void 动态表固定字段集合与NTCare定义一致()
    {
        string[] expected =
        [
            "id", "parent_table_id", "parent_table_name", "patient_id", "patient_event_id",
            "card_id", "card_sub_id", "parent_card_sub_id", "linked_card_sub_id", "card_name",
            "form_name", "form_set_name", "project_name", "form_id", "form_set_id", "project_id",
            "ward_name", "ward_id", "department_name", "department_id", "region_name", "region_id",
            "hospital_name", "hospital_id", "created_at", "created_by", "created_by_name", "updated_at",
            "updated_by", "updated_by_name", "is_valid"
        ];

        Assert.Equal(expected, FollowUpPackageSchemaCheckService.DynamicFixedColumns);
    }

    [Theory]
    [InlineData("target", "BusinessData")]
    [InlineData("care", "DynamicFormData")]
    public void 动态表分类与target模式不一致时拒绝处理(string schema, string category)
    {
        var manifest = CreateManifest("data/table.jsonl", 1, schema, "form_answers", category);

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.ValidateDynamicTableClassifications([manifest]));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
    }

    [Fact]
    public void 动态表活动项与无载荷项重复时要求结构复核()
    {
        var manifests = new[]
        {
            CreateManifest(
                "data/target_form_answers.jsonl",
                1,
                "target",
                "form_answers",
                "DynamicFormData"),
            CreateManifest(
                null,
                0,
                "target",
                "form_answers",
                "DynamicFormData")
        };

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.ValidateDynamicTableClassifications(manifests));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("重复", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 动态表两个无载荷项重复时要求结构复核()
    {
        var manifests = new[]
        {
            CreateManifest(
                null,
                0,
                "target",
                "form_answers",
                "DynamicFormData"),
            CreateManifest(
                null,
                0,
                "target",
                "form_answers",
                "DynamicFormData")
        };

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.ValidateDynamicTableClassifications(manifests));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("重复", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 非动态源表不得通过批准映射进入target模式()
    {
        var decision = new FollowUpSchemaDecision
        {
            DecisionStatus = "ApprovedMapping",
            TableMappings =
            {
                ["care.patient"] = new FollowUpTableMapping
                {
                    TargetSchema = "target",
                    TargetTable = "form_answers"
                }
            }
        };

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.SelectAndMapSourceTables(
                [CreateTable("uuid", false)],
                [CreateManifest("data/care_patient.jsonl", 1)],
                decision));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
    }

    [Fact]
    public void 动态源表不得通过批准映射离开target模式()
    {
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");
        var decision = new FollowUpSchemaDecision
        {
            DecisionStatus = "ApprovedMapping",
            TableMappings =
            {
                ["target.form_answers"] = new FollowUpTableMapping
                {
                    TargetSchema = "care",
                    TargetTable = "form_answers"
                }
            }
        };

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.SelectAndMapSourceTables(
                [CreateDynamicTable()],
                [manifest],
                decision));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
    }

    [Fact]
    public void 批准映射不得用大小写变体伪装target模式()
    {
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");
        var decision = new FollowUpSchemaDecision
        {
            DecisionStatus = "ApprovedMapping",
            TableMappings =
            {
                ["target.form_answers"] = new FollowUpTableMapping
                {
                    TargetSchema = "Target",
                    TargetTable = "form_answers"
                }
            }
        };

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.SelectAndMapSourceTables(
                [CreateDynamicTable()],
                [manifest],
                decision));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
    }

    [Fact]
    public void 表单项元数据直接生成动态字段范围和ARRAY兼容授权()
    {
        var source = CreateDynamicTable(
            ("file_question", "ARRAY"),
            ("choice_question", "text[]"),
            ("ordinary_question", "ARRAY"));
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");

        var build = FollowUpPackageSchemaCheckService.BuildDynamicColumnScopeDefinitions(
            new FollowUpSchemaSnapshot { Tables = [source] },
            [manifest],
            [
                new FollowUpQuestionReference("form_answers", "file_question", "文件"),
                new FollowUpQuestionReference("form_answers", "choice_question", "选择"),
                new FollowUpQuestionReference("form_answers", "ordinary_question", "文本")
            ],
            FollowUpQuestionScopeSource.Package,
            decision: null);

        var scope = Assert.Single(build.Scopes);
        Assert.Empty(build.BreakingMessages);
        Assert.Contains("file_question", scope.SourceColumns);
        Assert.Contains("choice_question", scope.SourceColumns);
        Assert.Contains("ordinary_question", scope.SourceColumns);
        Assert.Equal(["choice_question", "file_question"], scope.ArrayToTextSourceColumns);
        Assert.Equal(["choice_question", "file_question"], scope.ArrayToTextTargetColumns);
        Assert.Equal(["file_question"], scope.FileQuestionSourceColumns);
        Assert.Equal(["file_question"], scope.FileQuestionTargetColumns);
    }

    [Fact]
    public void 目标表单项回退也禁止动态标识重命名映射()
    {
        var source = CreateDynamicTable(("source_file", "ARRAY"));
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");
        var decision = new FollowUpSchemaDecision
        {
            DecisionStatus = "ApprovedMapping",
            TableMappings =
            {
                ["target.form_answers"] = new FollowUpTableMapping
                {
                    TargetSchema = "target",
                    TargetTable = "hospital_answers",
                    ColumnMappings = { ["source_file"] = "hospital_file" }
                }
            }
        };

        var build = FollowUpPackageSchemaCheckService.BuildDynamicColumnScopeDefinitions(
            new FollowUpSchemaSnapshot { Tables = [source] },
            [manifest],
            [new FollowUpQuestionReference("hospital_answers", "hospital_file", "文件")],
            FollowUpQuestionScopeSource.Target,
            decision);

        Assert.Contains(build.BreakingMessages, message =>
            message.Contains("医院端表单项快照", StringComparison.Ordinal)
            && message.Contains("表名映射", StringComparison.Ordinal));
        Assert.Contains(build.BreakingMessages, message =>
            message.Contains("医院端表单项快照", StringComparison.Ordinal)
            && message.Contains("字段映射", StringComparison.Ordinal));
    }

    [Fact]
    public void 包内表单项快照禁止动态表重命名映射()
    {
        var source = CreateDynamicTable(("source_question", "text"));
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");
        var decision = new FollowUpSchemaDecision
        {
            DecisionStatus = "ApprovedMapping",
            TableMappings =
            {
                ["target.form_answers"] = new FollowUpTableMapping
                {
                    TargetSchema = "target",
                    TargetTable = "hospital_answers"
                }
            }
        };

        var build = FollowUpPackageSchemaCheckService.BuildDynamicColumnScopeDefinitions(
            new FollowUpSchemaSnapshot { Tables = [source] },
            [manifest],
            [new FollowUpQuestionReference("form_answers", "source_question", "文本")],
            FollowUpQuestionScopeSource.Package,
            decision);

        Assert.Contains(build.BreakingMessages, message =>
            message.Contains("包内表单项快照", StringComparison.Ordinal)
            && message.Contains("表名映射", StringComparison.Ordinal));
    }

    [Fact]
    public void 包内表单项快照禁止动态字段重命名映射()
    {
        var source = CreateDynamicTable(("source_question", "text"));
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");
        var decision = new FollowUpSchemaDecision
        {
            DecisionStatus = "ApprovedMapping",
            TableMappings =
            {
                ["target.form_answers"] = new FollowUpTableMapping
                {
                    TargetSchema = "target",
                    TargetTable = "form_answers",
                    ColumnMappings = { ["source_question"] = "hospital_question" }
                }
            }
        };

        var build = FollowUpPackageSchemaCheckService.BuildDynamicColumnScopeDefinitions(
            new FollowUpSchemaSnapshot { Tables = [source] },
            [manifest],
            [new FollowUpQuestionReference("form_answers", "source_question", "文本")],
            FollowUpQuestionScopeSource.Package,
            decision);

        Assert.Contains(build.BreakingMessages, message =>
            message.Contains("包内表单项快照", StringComparison.Ordinal)
            && message.Contains("字段映射", StringComparison.Ordinal));
    }

    [Fact]
    public void 动态表和字段的大小写变体也不是恒等映射()
    {
        var source = CreateDynamicTable(("source_question", "text"));
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");
        var decision = new FollowUpSchemaDecision
        {
            DecisionStatus = "ApprovedMapping",
            TableMappings =
            {
                ["target.form_answers"] = new FollowUpTableMapping
                {
                    TargetSchema = "target",
                    TargetTable = "Form_Answers",
                    ColumnMappings = { ["source_question"] = "Source_Question" }
                }
            }
        };

        var build = FollowUpPackageSchemaCheckService.BuildDynamicColumnScopeDefinitions(
            new FollowUpSchemaSnapshot { Tables = [source] },
            [manifest],
            [new FollowUpQuestionReference("form_answers", "source_question", "文本")],
            FollowUpQuestionScopeSource.Package,
            decision);

        Assert.Contains(build.BreakingMessages, message =>
            message.Contains("表名映射", StringComparison.Ordinal));
        Assert.Contains(build.BreakingMessages, message =>
            message.Contains("字段映射", StringComparison.Ordinal));
    }

    [Fact]
    public void 动态字段映射源键的大小写变体也不是恒等映射()
    {
        var source = CreateDynamicTable(("source_question", "text"));
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");
        var decision = new FollowUpSchemaDecision
        {
            DecisionStatus = "ApprovedMapping",
            TableMappings =
            {
                ["target.form_answers"] = new FollowUpTableMapping
                {
                    TargetSchema = "target",
                    TargetTable = "form_answers",
                    ColumnMappings = { ["Source_Question"] = "Source_Question" }
                }
            }
        };

        var build = FollowUpPackageSchemaCheckService.BuildDynamicColumnScopeDefinitions(
            new FollowUpSchemaSnapshot { Tables = [source] },
            [manifest],
            [new FollowUpQuestionReference("form_answers", "source_question", "文本")],
            FollowUpQuestionScopeSource.Package,
            decision);

        Assert.Contains(build.BreakingMessages, message =>
            message.Contains("映射源字段", StringComparison.Ordinal)
            && message.Contains("Source_Question", StringComparison.Ordinal));
    }

    [Fact]
    public void 动态表人工决定键必须精确匹配源表大小写()
    {
        var source = CreateDynamicTable(("source_question", "text"));
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");
        var decision = new FollowUpSchemaDecision
        {
            DecisionStatus = "ApprovedMapping",
            TableMappings =
            {
                ["target.Form_Answers"] = new FollowUpTableMapping
                {
                    TargetSchema = "target",
                    TargetTable = "form_answers",
                    DefaultValues = { ["hospital_extension"] = JsonDocument.Parse("\"value\"").RootElement.Clone() }
                }
            }
        };

        var build = FollowUpPackageSchemaCheckService.BuildDynamicColumnScopeDefinitions(
            new FollowUpSchemaSnapshot { Tables = [source] },
            [manifest],
            [new FollowUpQuestionReference("form_answers", "source_question", "文本")],
            FollowUpQuestionScopeSource.Package,
            decision);

        Assert.Contains(build.BreakingMessages, message =>
            message.Contains("映射源表", StringComparison.Ordinal)
            && message.Contains("target.Form_Answers", StringComparison.Ordinal));
    }

    [Fact]
    public void 包内关联字段不在源结构时标记结构阻断()
    {
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");

        var build = FollowUpPackageSchemaCheckService.BuildDynamicColumnScopeDefinitions(
            new FollowUpSchemaSnapshot { Tables = [CreateDynamicTable()] },
            [manifest],
            [new FollowUpQuestionReference("form_answers", "missing_question", "文本")],
            FollowUpQuestionScopeSource.Package,
            decision: null);

        Assert.Contains(build.BreakingMessages, message =>
            message.Contains("missing_question", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Package")]
    [InlineData("Target")]
    public void 医院关联字段大小写必须精确匹配动态源结构(string sourceName)
    {
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");

        var build = FollowUpPackageSchemaCheckService.BuildDynamicColumnScopeDefinitions(
            new FollowUpSchemaSnapshot
            {
                Tables = [CreateDynamicTable(("source_question", "text"))]
            },
            [manifest],
            [new FollowUpQuestionReference("form_answers", "Source_Question", "文本")],
            Enum.Parse<FollowUpQuestionScopeSource>(sourceName),
            decision: null);

        var scope = Assert.Single(build.Scopes);
        Assert.DoesNotContain("source_question", scope.SourceColumns);
        Assert.Contains(build.BreakingMessages, message =>
            message.Contains("Source_Question", StringComparison.Ordinal));
    }

    [Fact]
    public void 医院关联动态表名大小写必须精确匹配源结构()
    {
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");

        var build = FollowUpPackageSchemaCheckService.BuildDynamicColumnScopeDefinitions(
            new FollowUpSchemaSnapshot
            {
                Tables = [CreateDynamicTable(("source_question", "text"))]
            },
            [manifest],
            [new FollowUpQuestionReference("Form_Answers", "source_question", "文本")],
            FollowUpQuestionScopeSource.Package,
            decision: null);

        var scope = Assert.Single(build.Scopes);
        Assert.DoesNotContain("source_question", scope.SourceColumns);
        Assert.Contains(build.BreakingMessages, message =>
            message.Contains("Form_Answers", StringComparison.Ordinal)
            && message.Contains("大小写", StringComparison.Ordinal));
    }

    [Fact]
    public void 动态目标字段大小写变体不能满足源字段结构()
    {
        var source = CreateDynamicTable(("source_question", "text"));
        var target = CreateDynamicTable(("Source_Question", "text"));
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");
        var scope = new FollowUpTableColumnScope(
            "target",
            "form_answers",
            "target",
            "form_answers",
            source.Columns.Select(item => item.Name).ToList(),
            source.Columns.Select(item => item.Name).ToList());

        var result = FollowUpPackageSchemaCheckService.Evaluate(
            [source],
            [target],
            [manifest],
            columnScopes: [scope]);

        Assert.False(result.Compatible);
        Assert.Equal("RequiresMapping", result.DiffLevel);
        Assert.Contains(result.Messages, message =>
            message.Contains("source_question", StringComparison.Ordinal));
    }

    [Fact]
    public void 动态数据行字段大小写必须精确匹配源结构()
    {
        Assert.Throws<InvalidDataException>(() =>
            FollowUpPackageSchemaCheckService.CollectIgnoredNonNullColumns(
                "target",
                "form_answers",
                ["{\"Source_Question\":\"value\"}"],
                new HashSet<string>(["source_question"], StringComparer.Ordinal),
                new HashSet<string>(["source_question"], StringComparer.Ordinal)));
    }

    [Theory]
    [InlineData(" form_answers", "source_question")]
    [InlineData("form_answers ", "source_question")]
    [InlineData("form_answers", " source_question")]
    [InlineData("form_answers", "source_question ")]
    public void 表单项数据库标识符不得通过Trim规范化(string tableName, string columnName)
    {
        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.CreateQuestionReference(
                tableName,
                columnName,
                " 文本 "));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("首尾空白", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bad-name", "source_question")]
    [InlineData("form_answers", "x.y")]
    public void 表单项非法数据库标识符要求结构复核而非内部失败(string tableName, string columnName)
    {
        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.CreateQuestionReference(
                tableName,
                columnName,
                "文本"));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("非法", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bad-table", "active_question")]
    [InlineData("form_answers", "bad-column")]
    public void 动态源结构非法标识符要求结构复核而非内部失败(
        string tableName,
        string columnName)
    {
        var source = CreateDynamicTable((columnName, "text"));
        source.TableName = tableName;
        var manifest = CreateManifest(
            "data/target_dynamic.jsonl",
            1,
            "target",
            tableName,
            "DynamicFormData");

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.BuildDynamicColumnScopeDefinitions(
                new FollowUpSchemaSnapshot { Tables = [source] },
                [manifest],
                [new FollowUpQuestionReference(tableName, columnName, "文本")],
                FollowUpQuestionScopeSource.Package,
                decision: null));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("非法", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 动态默认值非法标识符要求结构复核而非备份后内部失败()
    {
        var source = CreateDynamicTable(("active_question", "text"));
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");
        var decision = new FollowUpSchemaDecision
        {
            DecisionStatus = "ApprovedMapping",
            TableMappings =
            {
                ["target.form_answers"] = new FollowUpTableMapping
                {
                    TargetSchema = "target",
                    TargetTable = "form_answers",
                    DefaultValues =
                    {
                        ["bad-column"] = JsonDocument.Parse("\"value\"").RootElement.Clone()
                    }
                }
            }
        };

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.BuildDynamicColumnScopeDefinitions(
                new FollowUpSchemaSnapshot { Tables = [source] },
                [manifest],
                [new FollowUpQuestionReference("form_answers", "active_question", "文本")],
                FollowUpQuestionScopeSource.Package,
                decision));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("默认值", exception.Message, StringComparison.Ordinal);
        Assert.Contains("非法", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 表单项数据类型文本仍允许修剪首尾空白()
    {
        var reference = FollowUpPackageSchemaCheckService.CreateQuestionReference(
            "form_answers",
            "source_question",
            " 文本 ");

        Assert.NotNull(reference);
        Assert.Equal("文本", reference.DataType);
    }

    [Theory]
    [InlineData("{\"table_name\":null,\"column_name\":\"source_question\",\"data_type\":\"文本\"}")]
    [InlineData("{\"table_name\":\"\",\"column_name\":\"source_question\",\"data_type\":\"文本\"}")]
    [InlineData("{\"table_name\":\"form_answers\",\"column_name\":null,\"data_type\":\"文本\"}")]
    [InlineData("{\"table_name\":\"form_answers\",\"column_name\":\"\",\"data_type\":\"文本\"}")]
    public void 未绑定动态字段的表单项行不生成授权引用(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var reference = FollowUpPackageSchemaCheckService.CreateQuestionReference(
            ReadNullableString(root, "table_name"),
            ReadNullableString(root, "column_name"),
            ReadNullableString(root, "data_type"));

        Assert.Null(reference);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void 空数据类型仍授权字段但不授予ARRAY转text(string? dataType)
    {
        var reference = FollowUpPackageSchemaCheckService.CreateQuestionReference(
            "form_answers",
            "file_question",
            dataType);
        Assert.NotNull(reference);
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");

        var build = FollowUpPackageSchemaCheckService.BuildDynamicColumnScopeDefinitions(
            new FollowUpSchemaSnapshot
            {
                Tables = [CreateDynamicTable(("file_question", "ARRAY"))]
            },
            [manifest],
            [reference],
            FollowUpQuestionScopeSource.Package,
            decision: null);

        var scope = Assert.Single(build.Scopes);
        Assert.Empty(build.BreakingMessages);
        Assert.Contains("file_question", scope.SourceColumns);
        Assert.DoesNotContain("file_question", scope.ArrayToTextSourceColumns);
        Assert.DoesNotContain("file_question", scope.ArrayToTextTargetColumns);
    }

    [Fact]
    public async Task 未绑定动态字段的表单项行仍计入记录数和原始内容hash()
    {
        var stagingPath = Path.Combine(
            Path.GetTempPath(),
            $"followup-question-reference-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingPath);
        try
        {
            var hospitalId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var rows = new[]
            {
                $$"""{"id":"{{Guid.NewGuid()}}","hospital_id":"{{hospitalId}}","project_id":"{{projectId}}","table_name":null,"column_name":null,"data_type":null}""",
                $$"""{"id":"{{Guid.NewGuid()}}","hospital_id":"{{hospitalId}}","project_id":"{{projectId}}","table_name":"","column_name":"","data_type":""}""",
                $$"""{"id":"{{Guid.NewGuid()}}","hospital_id":"{{hospitalId}}","project_id":"{{projectId}}","table_name":"form_answers","column_name":"active_question","data_type":"文本"}"""
            };
            const string exportPath = "form_form_question.jsonl";
            const string projectExportPath = "form_form_project.jsonl";
            var filePath = Path.Combine(stagingPath, exportPath);
            var projectPath = Path.Combine(stagingPath, projectExportPath);
            await File.WriteAllLinesAsync(filePath, rows, new UTF8Encoding(false));
            await File.WriteAllLinesAsync(
                projectPath,
                [$$"""{"id":"{{projectId}}","hospital_id":"{{hospitalId}}"}"""],
                new UTF8Encoding(false));
            var item = CreateManifest(exportPath, rows.Length, "form", "form_question");
            item.FileHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(filePath)))
                .ToLowerInvariant();
            item.ContentHash = FollowUpPackageSchemaCheckService.ComputeQuestionContentHash(rows);
            var projectItem = CreateManifest(projectExportPath, 1, "form", "form_project");
            projectItem.Required = true;
            projectItem.FileHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(projectPath)))
                .ToLowerInvariant();
            projectItem.ContentHash = projectItem.FileHash;
            var package = new FollowUpVerifiedPackage(
                PackagePath: string.Empty,
                PackageHash: string.Empty,
                StagingPath: stagingPath,
                Envelope: new FollowUpEncryptedEnvelope(),
                Manifest: new FollowUpPackageManifest { HospitalId = hospitalId },
                TableManifest: [item, projectItem],
                SchemaSnapshot: new FollowUpSchemaSnapshot(),
                SchemaDiff: new FollowUpSchemaDiff());
            var method = typeof(FollowUpPackageSchemaCheckService).GetMethod(
                "ReadPackageQuestionScopeAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var task = Assert.IsAssignableFrom<Task<PackageQuestionScopeSnapshot>>(method.Invoke(
                null,
                [package, item, CancellationToken.None]));

            var references = (await task).References;

            var reference = Assert.Single(references);
            Assert.Equal("active_question", reference.ColumnName);
            Assert.Equal(
                FollowUpPackageSchemaCheckService.ComputeQuestionContentHash(rows),
                item.ContentHash);
        }
        finally
        {
            Directory.Delete(stagingPath, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 包内表单项所属项目跨医院时必须阻断(bool hasDynamicBinding)
    {
        var stagingPath = Path.Combine(
            Path.GetTempPath(),
            $"followup-question-project-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingPath);
        try
        {
            var hospitalId = Guid.NewGuid();
            var otherHospitalId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var questionRows = new[]
            {
                hasDynamicBinding
                    ? $$"""{"id":"{{Guid.NewGuid()}}","hospital_id":"{{hospitalId}}","project_id":"{{projectId}}","table_name":"form_answers","column_name":"active_question","data_type":"文本"}"""
                    : $$"""{"id":"{{Guid.NewGuid()}}","hospital_id":"{{hospitalId}}","project_id":"{{projectId}}","table_name":null,"column_name":null,"data_type":null}"""
            };
            var projectRows = new[]
            {
                $$"""{"id":"{{projectId}}","hospital_id":"{{otherHospitalId}}"}"""
            };
            const string questionExportPath = "form_form_question.jsonl";
            const string projectExportPath = "form_form_project.jsonl";
            var questionPath = Path.Combine(stagingPath, questionExportPath);
            var projectPath = Path.Combine(stagingPath, projectExportPath);
            await File.WriteAllLinesAsync(questionPath, questionRows, new UTF8Encoding(false));
            await File.WriteAllLinesAsync(projectPath, projectRows, new UTF8Encoding(false));
            var questionItem = CreateManifest(
                questionExportPath,
                questionRows.Length,
                "form",
                "form_question");
            questionItem.FileHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(questionPath)))
                .ToLowerInvariant();
            questionItem.ContentHash = questionItem.FileHash;
            var projectItem = CreateManifest(
                projectExportPath,
                projectRows.Length,
                "form",
                "form_project");
            projectItem.Required = true;
            projectItem.FileHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(projectPath)))
                .ToLowerInvariant();
            projectItem.ContentHash = projectItem.FileHash;
            var package = new FollowUpVerifiedPackage(
                PackagePath: string.Empty,
                PackageHash: string.Empty,
                StagingPath: stagingPath,
                Envelope: new FollowUpEncryptedEnvelope(),
                Manifest: new FollowUpPackageManifest { HospitalId = hospitalId },
                TableManifest: [questionItem, projectItem],
                SchemaSnapshot: new FollowUpSchemaSnapshot(),
                SchemaDiff: new FollowUpSchemaDiff());
            var method = typeof(FollowUpPackageSchemaCheckService).GetMethod(
                "ReadPackageQuestionScopeAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var task = Assert.IsAssignableFrom<Task<PackageQuestionScopeSnapshot>>(method.Invoke(
                null,
                [package, questionItem, CancellationToken.None]));

            var exception = await Assert.ThrowsAsync<FollowUpPackageException>(async () => await task);

            Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
            Assert.Contains("项目", exception.Message, StringComparison.Ordinal);
            Assert.Contains("医院", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(stagingPath, recursive: true);
        }
    }

    [Fact]
    public async Task 无动态答案文件时包内题目仍校验项目医院归属()
    {
        var stagingPath = Path.Combine(
            Path.GetTempPath(),
            $"followup-question-without-dynamic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingPath);
        try
        {
            var hospitalId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var package = await CreateQuestionPackageAsync(
                stagingPath,
                hospitalId,
                [$$"""{"id":"{{Guid.NewGuid()}}","hospital_id":"{{hospitalId}}","project_id":"{{projectId}}","table_name":"form_answers","column_name":"active_question","data_type":"文本"}"""],
                [$$"""{"id":"{{projectId}}","hospital_id":"{{Guid.NewGuid()}}"}"""]);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:CubeDb"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused"
                })
                .Build();
            var service = new FollowUpPackageSchemaCheckService(configuration);
            var method = typeof(FollowUpPackageSchemaCheckService).GetMethod(
                "BuildDynamicColumnScopesAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var task = Assert.IsAssignableFrom<Task>(method.Invoke(
                service,
                [package, null, null, CancellationToken.None]));

            var exception = await Assert.ThrowsAsync<FollowUpPackageException>(async () => await task);

            Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
            Assert.Contains("项目", exception.Message, StringComparison.Ordinal);
            Assert.Contains("医院", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(stagingPath, recursive: true);
        }
    }

    [Fact]
    public async Task 项目增量文件只覆盖部分题目项目时其余项目交由目标端复验()
    {
        var stagingPath = Path.Combine(
            Path.GetTempPath(),
            $"followup-partial-project-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingPath);
        try
        {
            var hospitalId = Guid.NewGuid();
            var packageProjectId = Guid.NewGuid();
            var targetProjectId = Guid.NewGuid();
            var packageQuestionId = Guid.NewGuid();
            var targetQuestionId = Guid.NewGuid();
            var package = await CreateQuestionPackageAsync(
                stagingPath,
                hospitalId,
                [
                    $$"""{"id":"{{packageQuestionId}}","hospital_id":"{{hospitalId}}","project_id":"{{packageProjectId}}","table_name":"form_answers","column_name":"first_question","data_type":"文本"}""",
                    $$"""{"id":"{{targetQuestionId}}","hospital_id":"{{hospitalId}}","project_id":"{{targetProjectId}}","table_name":"form_answers","column_name":"second_question","data_type":"文本"}"""
                ],
                [$$"""{"id":"{{packageProjectId}}","hospital_id":"{{hospitalId}}"}"""]);
            var questionItem = package.TableManifest.Single(item => item.TableName == "form_question");

            var snapshot = await FollowUpPackageSchemaCheckService.ReadPackageQuestionScopeAsync(
                package,
                questionItem,
                CancellationToken.None);

            Assert.Equal(2, snapshot.ProjectIds.Count);
            Assert.Equal(
                new[] { packageQuestionId, targetQuestionId }.Order(),
                snapshot.QuestionIds.Order());
            var packageProjectIdsProperty = snapshot.GetType().GetProperty("PackageProjectIds");
            Assert.NotNull(packageProjectIdsProperty);
            var packageProjectIds = Assert.IsAssignableFrom<IReadOnlyCollection<Guid>>(
                packageProjectIdsProperty.GetValue(snapshot));
            Assert.Equal([packageProjectId], packageProjectIds.Order().ToArray());
            var targetProjectIdsProperty = snapshot.GetType().GetProperty("TargetProjectIds");
            Assert.NotNull(targetProjectIdsProperty);
            var targetProjectIds = Assert.IsAssignableFrom<IReadOnlyCollection<Guid>>(
                targetProjectIdsProperty.GetValue(snapshot));
            Assert.Equal([targetProjectId], targetProjectIds.Order().ToArray());

            var guardFactory = typeof(FollowUpPackageImportService).GetMethod(
                "CreatePackageQuestionProjectGuard",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(guardFactory);
            var guard = Assert.IsType<PackageQuestionProjectGuard>(guardFactory.Invoke(null, [snapshot]));
            Assert.Equal(
                new[] { packageQuestionId, targetQuestionId }.Order(),
                guard.QuestionIds.Order());
            Assert.Equal(new[] { packageProjectId, targetProjectId }.Order(), guard.ProjectIds.Order());
            var guardedPackageProjectIds = Assert.IsAssignableFrom<IReadOnlyCollection<Guid>>(
                guard.GetType().GetProperty("PackageProjectIds")?.GetValue(guard));
            Assert.Equal([packageProjectId], guardedPackageProjectIds.Order().ToArray());
        }
        finally
        {
            Directory.Delete(stagingPath, recursive: true);
        }
    }

    [Fact]
    public async Task 项目增量覆盖全部题目项目时仍创建事务保护范围()
    {
        var stagingPath = Path.Combine(
            Path.GetTempPath(),
            $"followup-covered-project-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingPath);
        try
        {
            var hospitalId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var package = await CreateQuestionPackageAsync(
                stagingPath,
                hospitalId,
                [$$"""{"id":"{{Guid.NewGuid()}}","hospital_id":"{{hospitalId}}","project_id":"{{projectId}}","table_name":"form_answers","column_name":"active_question","data_type":"文本"}"""],
                [$$"""{"id":"{{projectId}}","hospital_id":"{{hospitalId}}"}"""]);
            var questionItem = package.TableManifest.Single(item => item.TableName == "form_question");
            var snapshot = await FollowUpPackageSchemaCheckService.ReadPackageQuestionScopeAsync(
                package,
                questionItem,
                CancellationToken.None);

            var guard = FollowUpPackageImportService.CreatePackageQuestionProjectGuard(snapshot);

            Assert.NotNull(guard);
            Assert.Equal([projectId], guard.ProjectIds);
        }
        finally
        {
            Directory.Delete(stagingPath, recursive: true);
        }
    }

    [Fact]
    public async Task 仅携带项目增量时仍解析医院范围并创建写前守卫()
    {
        var stagingPath = Path.Combine(
            Path.GetTempPath(),
            $"followup-project-only-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingPath);
        try
        {
            var hospitalId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            const string exportPath = "form_form_project.jsonl";
            var filePath = Path.Combine(stagingPath, exportPath);
            await File.WriteAllLinesAsync(
                filePath,
                [$$"""{"id":"{{projectId}}","hospital_id":"{{hospitalId}}"}"""],
                new UTF8Encoding(false));
            var projectItem = CreateManifest(exportPath, 1, "form", "form_project");
            projectItem.Required = true;
            projectItem.FileHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(filePath)))
                .ToLowerInvariant();
            projectItem.ContentHash = projectItem.FileHash;
            var package = new FollowUpVerifiedPackage(
                PackagePath: string.Empty,
                PackageHash: string.Empty,
                StagingPath: stagingPath,
                Envelope: new FollowUpEncryptedEnvelope(),
                Manifest: new FollowUpPackageManifest { HospitalId = hospitalId },
                TableManifest: [projectItem],
                SchemaSnapshot: new FollowUpSchemaSnapshot(),
                SchemaDiff: new FollowUpSchemaDiff());

            var snapshot = await FollowUpPackageSchemaCheckService.ReadPackageProjectScopeAsync(
                package,
                CancellationToken.None);
            var guard = FollowUpPackageImportService.CreatePackageProjectGuard(snapshot);

            Assert.Equal([projectId], snapshot.ProjectIds);
            Assert.NotNull(guard);
            Assert.Empty(guard.QuestionIds);
            Assert.Equal([projectId], guard.ProjectIds);
            Assert.Equal([projectId], guard.PackageProjectIds);
        }
        finally
        {
            Directory.Delete(stagingPath, recursive: true);
        }
    }

    [Fact]
    public void 权威空表单项范围仍保留动态表系统固定字段()
    {
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");

        var build = FollowUpPackageSchemaCheckService.BuildDynamicColumnScopeDefinitions(
            new FollowUpSchemaSnapshot { Tables = [CreateDynamicTable(("stale_question", "text"))] },
            [manifest],
            [],
            FollowUpQuestionScopeSource.Empty,
            decision: null);

        var scope = Assert.Single(build.Scopes);
        Assert.Empty(build.BreakingMessages);
        Assert.Equal(FollowUpPackageSchemaCheckService.DynamicFixedColumns, scope.SourceColumns);
        Assert.DoesNotContain("stale_question", scope.SourceColumns);
    }

    [Fact]
    public void 未关联历史动态字段不阻断医院授权范围()
    {
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");

        var build = FollowUpPackageSchemaCheckService.BuildDynamicColumnScopeDefinitions(
            new FollowUpSchemaSnapshot
            {
                Tables =
                [
                    CreateDynamicTable(
                        ("active_question", "text"),
                        ("1_month_follow_up_laboratory_test_date", "text"))
                ]
            },
            [manifest],
            [new FollowUpQuestionReference("form_answers", "active_question", "文本")],
            FollowUpQuestionScopeSource.Package,
            decision: null);

        var scope = Assert.Single(build.Scopes);
        Assert.Empty(build.BreakingMessages);
        Assert.Contains("active_question", scope.SourceColumns);
        Assert.DoesNotContain("1_month_follow_up_laboratory_test_date", scope.SourceColumns);
    }

    [Fact]
    public void 医院端表单项和所属项目必须同属当前医院()
    {
        var hospitalId = Guid.NewGuid();

        FollowUpPackageSchemaCheckService.ValidateQuestionHospitalScope(
            hospitalId,
            hospitalId,
            hospitalId);

        Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.ValidateQuestionHospitalScope(
                hospitalId,
                Guid.NewGuid(),
                hospitalId));
        Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.ValidateQuestionHospitalScope(
                hospitalId,
                hospitalId,
                Guid.NewGuid()));
        Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.ValidateQuestionHospitalScope(
                hospitalId,
                null,
                hospitalId));
        Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.ValidateQuestionHospitalScope(
                hospitalId,
                hospitalId,
                null));
    }

    [Fact]
    public void 动态表ARRAY字段允许写入text但固定表仍拒绝()
    {
        var dynamicManifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");
        var scope = CreateDynamicScope("file_question");
        scope.ArrayToTextSourceColumns.Add("file_question");
        scope.ArrayToTextTargetColumns.Add("file_question");
        var dynamicSource = FollowUpPackageSchemaCheckService.SelectAndMapSourceTables(
            [CreateDynamicTable(("file_question", "ARRAY"))],
            [dynamicManifest],
            decision: null,
            [scope]);
        var dynamicResult = FollowUpPackageSchemaCheckService.Evaluate(
            dynamicSource,
            [CreateDynamicTable(("file_question", "text"))],
            [dynamicManifest],
            columnScopes: [scope]);
        var unauthorizedScope = CreateDynamicScope("file_question");
        var unauthorizedResult = FollowUpPackageSchemaCheckService.Evaluate(
            FollowUpPackageSchemaCheckService.SelectAndMapSourceTables(
                [CreateDynamicTable(("file_question", "ARRAY"))],
                [dynamicManifest],
                decision: null,
                [unauthorizedScope]),
            [CreateDynamicTable(("file_question", "text"))],
            [dynamicManifest],
            columnScopes: [unauthorizedScope]);

        var fixedResult = FollowUpPackageSchemaCheckService.Evaluate(
            [CreateTable("ARRAY", false)],
            [CreateTable("text", false)],
            [CreateManifest("data/care_patient.jsonl", 1)]);

        Assert.True(dynamicResult.Compatible);
        Assert.False(unauthorizedResult.Compatible);
        Assert.Equal("Breaking", unauthorizedResult.DiffLevel);
        Assert.False(fixedResult.Compatible);
        Assert.Equal("Breaking", fixedResult.DiffLevel);
    }

    [Fact]
    public void 实际写入列不会超出结构校验范围和批准默认值()
    {
        var source = CreateDynamicTable(
            ("active_question", "text"),
            ("historical_question", "text"));
        var scope = CreateDynamicScope("active_question");
        var scoped = FollowUpPackageSchemaCheckService.MapAndApplySourceTable(
            source,
            decision: null,
            scope);
        var columns = FollowUpPackageImportService.ResolveWriteColumns(
            scoped,
            scoped.Columns.Select(item => item.Name).Append("approved_default").ToHashSet(StringComparer.OrdinalIgnoreCase),
            ["approved_default"]);
        var sql = FollowUpUpsertSqlBuilder.Build(
            scoped.SchemaName,
            scoped.TableName,
            columns,
            scoped.PrimaryKey,
            "Upsert");

        Assert.Contains("\"active_question\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"approved_default\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("historical_question", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void 动态范围字段或批准默认值不可写时拒绝导入()
    {
        var scoped = CreateDynamicTable(("active_question", "text"));
        var writable = scoped.Columns.Select(item => item.Name)
            .Where(item => item != "active_question")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageImportService.ResolveWriteColumns(
                scoped,
                writable,
                ["approved_default"]));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("active_question", exception.Message, StringComparison.Ordinal);
        Assert.Contains("approved_default", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 未关联非空字段按表字段统计且不记录字段值()
    {
        var audit = FollowUpPackageSchemaCheckService.CollectIgnoredNonNullColumns(
            "target",
            "form_answers",
            [
                "{\"id\":\"1\",\"active_question\":null,\"historical_question\":\"敏感值\"}",
                "{\"id\":\"2\",\"historical_question\":null}",
                "{\"id\":\"3\",\"historical_question\":[\"另一个值\"]}"
            ],
            new HashSet<string>(["id", "active_question"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["id", "active_question", "historical_question"], StringComparer.OrdinalIgnoreCase));

        var item = Assert.Single(audit);
        Assert.Equal("historical_question", item.ColumnName);
        Assert.Equal(2, item.NonNullRowCount);
        Assert.DoesNotContain("敏感值", JsonSerializer.Serialize(audit), StringComparison.Ordinal);
    }

    [Fact]
    public void ARRAY转text只接受字符串或null组成的JSON数组()
    {
        FollowUpPackageSchemaCheckService.ValidateArrayToTextValues(
            [
                "{\"file_question\":null}",
                "{\"file_question\":[]}",
                "{\"file_question\":[\"中文.pdf\",null,\"a\\\\b\\\"c\"]}"
            ],
            new HashSet<string>(["file_question"], StringComparer.OrdinalIgnoreCase));

        Assert.Throws<InvalidDataException>(() =>
            FollowUpPackageSchemaCheckService.ValidateArrayToTextValues(
                ["{\"file_question\":[1]}"],
                new HashSet<string>(["file_question"], StringComparer.OrdinalIgnoreCase)));
        Assert.Throws<InvalidDataException>(() =>
            FollowUpPackageSchemaCheckService.ValidateArrayToTextValues(
                ["{\"file_question\":{\"path\":\"a\"}}"],
                new HashSet<string>(["file_question"], StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void 实际导入行必须在已验证文件句柄消费阶段再次校验ARRAY转text值形状()
    {
        var arrayColumns = new HashSet<string>(["file_question"], StringComparer.Ordinal);

        FollowUpPackageSchemaCheckService.ValidateImportRow(
            "{\"file_question\":[\"report.pdf\",null]}",
            arrayColumns);
        Assert.Throws<InvalidDataException>(() =>
            FollowUpPackageSchemaCheckService.ValidateImportRow(
                "{\"file_question\":{\"path\":\"report.pdf\"}}",
                arrayColumns));

    }

    [Fact]
    public void 动态行出现结构快照外字段时拒绝继续处理()
    {
        Assert.Throws<InvalidDataException>(() =>
            FollowUpPackageSchemaCheckService.CollectIgnoredNonNullColumns(
                "target",
                "form_answers",
                ["{\"id\":\"1\",\"unknown_question\":\"value\"}"],
                new HashSet<string>(["id"], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(["id"], StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void 表单项范围来源必须由包文件或已导入主链哈希证明()
    {
        var item = CreateManifest(
            "data/form_form_question.jsonl",
            3,
            "form",
            "form_question");
        item.ContentHash = "package-hash";
        Assert.Equal(
            FollowUpQuestionScopeSource.Package,
            FollowUpPackageSchemaCheckService.ResolveQuestionScopeSource("Incremental", item, importedContentHash: null));

        item.ExportPath = null;
        item.RecordCount = 0;
        Assert.Equal(
            FollowUpQuestionScopeSource.Target,
            FollowUpPackageSchemaCheckService.ResolveQuestionScopeSource("Incremental", item, "package-hash"));
        Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.ResolveQuestionScopeSource("Incremental", item, "different-hash"));

        item.ContentHash = FollowUpPackageSchemaCheckService.EmptyFileSha256;
        Assert.Equal(
            FollowUpQuestionScopeSource.Empty,
            FollowUpPackageSchemaCheckService.ResolveQuestionScopeSource("Baseline", item, importedContentHash: null));
    }

    [Fact]
    public void 目标表单项实际hash与manifest不一致时拒绝回退()
    {
        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.EnsureTargetQuestionContentHash(
                "manifest-hash",
                ["live-target-hash"]));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("实际内容 hash", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 目标表单项内容hash使用UTF8和固定LF计算()
    {
        var rows = new[]
        {
            "{\"column_name\":\"中文字段\",\"table_name\":\"answers\"}",
            "{\"column_name\":\"quoted\\\"field\",\"table_name\":\"answers\"}"
        };
        var expected = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join('\n', rows) + "\n")))
            .ToLowerInvariant();

        var actual = FollowUpPackageSchemaCheckService.ComputeQuestionContentHash(rows);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void 目标表单项内容hash兼容云端LF和CRLF换行()
    {
        var rows = new[]
        {
            "{\"column_name\":\"first\",\"table_name\":\"answers\"}",
            "{\"column_name\":\"second\",\"table_name\":\"answers\"}"
        };
        var lfHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join('\n', rows) + "\n")))
            .ToLowerInvariant();
        var crlfHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join("\r\n", rows) + "\r\n")))
            .ToLowerInvariant();

        var actualHashes = FollowUpPackageSchemaCheckService.ComputeQuestionContentHashes(rows);

        Assert.Contains(lfHash, actualHashes);
        Assert.Contains(crlfHash, actualHashes);
        FollowUpPackageSchemaCheckService.EnsureTargetQuestionContentHash(lfHash, actualHashes);
        FollowUpPackageSchemaCheckService.EnsureTargetQuestionContentHash(crlfHash, actualHashes);
    }

    [Theory]
    [InlineData("Target", true)]
    [InlineData("Empty", true)]
    [InlineData("Package", false)]
    public void 仅目标端和空快照范围需要事务内实时复验(
        string sourceName,
        bool requiresGuard)
    {
        var source = Enum.Parse<FollowUpQuestionScopeSource>(sourceName);
        var guard = FollowUpPackageImportService.CreateTargetQuestionScopeGuard(
            source,
            "manifest-hash",
            ["id", "hospital_id", "table_name", "column_name", "data_type"]);

        Assert.Equal(requiresGuard, guard is not null);
        if (guard is null)
            return;
        Assert.Equal("manifest-hash", guard.ExpectedContentHash);
        Assert.Equal(
            ["id", "hospital_id", "table_name", "column_name", "data_type"],
            guard.SourceColumns);
    }

    [Fact]
    public void 目标表单项范围锁SQL设置三十秒有限等待()
    {
        var field = typeof(FollowUpPackageSchemaCheckService).GetField(
            "TargetQuestionScopeLockSql",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var sql = Assert.IsType<string>(field.GetRawConstantValue());

        Assert.Contains("SET LOCAL lock_timeout = '30s'", sql, StringComparison.Ordinal);
        Assert.Contains(
            "LOCK TABLE form.form_project, form.form_question IN SHARE MODE",
            sql,
            StringComparison.Ordinal);
        var limitedAt = sql.IndexOf("SET LOCAL lock_timeout = '30s'", StringComparison.Ordinal);
        var lockedAt = sql.IndexOf(
            "LOCK TABLE form.form_project, form.form_question IN SHARE MODE",
            StringComparison.Ordinal);
        var restoredAt = sql.IndexOf("SET LOCAL lock_timeout = '0'", StringComparison.Ordinal);
        Assert.True(limitedAt < lockedAt && lockedAt < restoredAt);
    }

    [Fact]
    public void Package省略项目快照时项目范围锁SQL设置三十秒有限等待()
    {
        var sql = FollowUpPackageSchemaCheckService.PackageQuestionProjectScopeLockSql;

        Assert.Contains("SET LOCAL lock_timeout = '30s'", sql, StringComparison.Ordinal);
        Assert.Contains("LOCK TABLE form.form_project, form.form_question IN SHARE MODE", sql, StringComparison.Ordinal);
        var limitedAt = sql.IndexOf("SET LOCAL lock_timeout = '30s'", StringComparison.Ordinal);
        var lockedAt = sql.IndexOf("LOCK TABLE form.form_project, form.form_question IN SHARE MODE", StringComparison.Ordinal);
        var restoredAt = sql.IndexOf("SET LOCAL lock_timeout = '0'", StringComparison.Ordinal);
        Assert.True(limitedAt < lockedAt && lockedAt < restoredAt);
    }

    [Fact]
    public void 包内项目写入前允许目标缺失但拒绝已存在的跨医院同ID()
    {
        var method = typeof(FollowUpPackageSchemaCheckService).GetMethod(
            "ValidateExistingProjectHospitalScope",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var hospitalId = Guid.NewGuid();
        var existingProjectId = Guid.NewGuid();
        var missingProjectId = Guid.NewGuid();

        method.Invoke(
            null,
            [
                hospitalId,
                new[] { existingProjectId, missingProjectId },
                new Dictionary<Guid, Guid?> { [existingProjectId] = hospitalId }
            ]);

        var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(
            null,
            [
                hospitalId,
                new[] { existingProjectId, missingProjectId },
                new Dictionary<Guid, Guid?> { [existingProjectId] = Guid.NewGuid() }
            ]));
        var packageException = Assert.IsType<FollowUpPackageException>(exception.InnerException);
        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, packageException.ErrorCode);
        Assert.Contains("不属于当前医院", packageException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 包内题目写入前允许目标缺失但拒绝既有跨医院同ID题目或项目()
    {
        var method = typeof(FollowUpPackageSchemaCheckService).GetMethod(
            "ValidateExistingQuestionHospitalScope",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var hospitalId = Guid.NewGuid();
        var existingQuestionId = Guid.NewGuid();
        var missingQuestionId = Guid.NewGuid();

        method.Invoke(
            null,
            [
                hospitalId,
                new[] { existingQuestionId, missingQuestionId },
                new Dictionary<Guid, (Guid? QuestionHospitalId, Guid? ProjectHospitalId)>
                {
                    [existingQuestionId] = (hospitalId, hospitalId)
                }
            ]);

        foreach (var invalidScope in new[]
                 {
                     (QuestionHospitalId: (Guid?)Guid.NewGuid(), ProjectHospitalId: (Guid?)hospitalId),
                     (QuestionHospitalId: (Guid?)hospitalId, ProjectHospitalId: (Guid?)Guid.NewGuid())
                 })
        {
            var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(
                null,
                [
                    hospitalId,
                    new[] { existingQuestionId, missingQuestionId },
                    new Dictionary<Guid, (Guid? QuestionHospitalId, Guid? ProjectHospitalId)>
                    {
                        [existingQuestionId] = invalidScope
                    }
                ]));
            var packageException = Assert.IsType<FollowUpPackageException>(exception.InnerException);
            Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, packageException.ErrorCode);
            Assert.Contains("不属于当前医院", packageException.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 项目和题目显式依赖排序不得把项目提前越过其他基础表()
    {
        var question = CreateManifest(
            "form_form_question.jsonl",
            1,
            "form",
            "form_question",
            "Relationship");
        var dynamic = CreateManifest(
            "target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");
        var project = CreateManifest(
            "form_form_project.jsonl",
            1,
            "form",
            "form_project",
            "Relationship");
        project.ImportPolicy = "UseExistingById";
        var department = CreateManifest(
            "system_sys_department.jsonl",
            1,
            "system",
            "sys_department",
            "Relationship");
        var method = typeof(FollowUpPackageImportService).GetMethod(
            "OrderImportTables",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var ordered = Assert.IsAssignableFrom<IReadOnlyList<FollowUpTableManifestItem>>(
            method.Invoke(null, [new[] { question, department, dynamic, project }]));

        var orderedList = ordered.ToList();
        var projectIndex = orderedList.IndexOf(project);
        Assert.True(orderedList.IndexOf(department) < projectIndex);
        Assert.True(projectIndex < orderedList.IndexOf(question));
        Assert.True(projectIndex < orderedList.IndexOf(dynamic));
    }

    [Theory]
    [InlineData("form", "form_project", "archive", "project")]
    [InlineData("care", "patient", "form", "form_project")]
    [InlineData("form", "form_question", "archive", "question")]
    [InlineData("care", "patient", "form", "form_question")]
    [InlineData("form", "form_project", "Form", "form_project")]
    [InlineData("form", "form_question", "form", "Form_Question")]
    public void 项目题目安全表不得通过批准映射进出(
        string sourceSchema,
        string sourceTable,
        string targetSchema,
        string targetTable)
    {
        var source = CreateManifest("data.jsonl", 1, sourceSchema, sourceTable);
        source.ImportPolicy = "UseExistingById";
        var mapped = CreateManifest("data.jsonl", 1, targetSchema, targetTable);
        mapped.ImportPolicy = source.ImportPolicy;
        var method = typeof(FollowUpPackageSchemaCheckService).GetMethod(
            "ValidateProtectedQuestionProjectMapping",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var exception = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(null, [source, mapped]));

        var packageException = Assert.IsType<FollowUpPackageException>(exception.InnerException);
        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, packageException.ErrorCode);
        Assert.Contains("映射", packageException.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("form_project", "hospital_id")]
    [InlineData("form_question", "project_id")]
    public void 项目题目安全字段不得通过批准映射改名(string tableName, string protectedColumn)
    {
        var source = new FollowUpTableSchema
        {
            SchemaName = "form",
            TableName = tableName,
            Columns =
            [
                new FollowUpColumnSchema { Name = "id", DataType = "uuid", OrdinalPosition = 1 },
                new FollowUpColumnSchema { Name = protectedColumn, DataType = "uuid", OrdinalPosition = 2 }
            ],
            PrimaryKey = ["id"]
        };
        var manifest = CreateManifest($"form_{tableName}.jsonl", 1, "form", tableName);
        var decision = new FollowUpSchemaDecision
        {
            DecisionStatus = "ApprovedMapping",
            TableMappings =
            {
                [$"form.{tableName}"] = new FollowUpTableMapping
                {
                    TargetSchema = "form",
                    TargetTable = tableName,
                    ColumnMappings = { [protectedColumn] = $"mapped_{protectedColumn}" }
                }
            }
        };

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.SelectAndMapSourceTables(
                [source],
                [manifest],
                decision));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains(protectedColumn, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hospital_id", "Hospital_Id")]
    [InlineData("Hospital_Id", "Hospital_Id")]
    [InlineData("Hospital_Id", "hospital_id")]
    [InlineData("id", "ID")]
    [InlineData("ID", "ID")]
    [InlineData("ID", "id")]
    public void 项目归属字段映射键值的大小写伪恒等也必须阻断(string sourceColumn, string targetColumn)
    {
        var source = new FollowUpTableSchema
        {
            SchemaName = "form",
            TableName = "form_project",
            Columns =
            [
                new FollowUpColumnSchema { Name = "id", DataType = "uuid", OrdinalPosition = 1 },
                new FollowUpColumnSchema { Name = "hospital_id", DataType = "uuid", OrdinalPosition = 2 }
            ],
            PrimaryKey = ["id"]
        };
        var manifest = CreateManifest("form_form_project.jsonl", 1, "form", "form_project");
        var decision = new FollowUpSchemaDecision
        {
            DecisionStatus = "ApprovedMapping",
            TableMappings =
            {
                ["form.form_project"] = new FollowUpTableMapping
                {
                    TargetSchema = "form",
                    TargetTable = "form_project",
                    ColumnMappings = { [sourceColumn] = targetColumn }
                }
            }
        };

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.SelectAndMapSourceTables(
                [source],
                [manifest],
                decision));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("归属字段", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("form_project", "external_id", "id")]
    [InlineData("form_project", "id", "external_id")]
    [InlineData("form_question", "ID", "id")]
    public void 项目题目安全表的源结构和清单主键必须精确为单列id(
        string tableName,
        string sourcePrimaryKey,
        string manifestPrimaryKey)
    {
        var source = CreateProtectedTableSchema(tableName);
        source.PrimaryKey = [sourcePrimaryKey];
        var manifest = CreateManifest($"form_{tableName}.jsonl", 1, "form", tableName);
        manifest.PrimaryKey = [manifestPrimaryKey];
        manifest.ImportPolicy = "Upsert";
        var mappedSchema = FollowUpSchemaDecisionProcessor.MapSchema(source, null);
        var mappedManifest = FollowUpSchemaDecisionProcessor.MapManifest(manifest, null);

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.ValidateProtectedQuestionProjectImportContract(
                source,
                manifest,
                mappedSchema,
                mappedManifest));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("主键", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UseExistingById")]
    [InlineData("RejectIfMissing")]
    [InlineData("InsertIfMissing")]
    [InlineData("upsert")]
    public void Package题目授权快照必须以精确Upsert策略落到目标表(string importPolicy)
    {
        var source = CreateProtectedTableSchema("form_question");
        var manifest = CreateManifest("form_form_question.jsonl", 1, "form", "form_question");
        manifest.PrimaryKey = ["id"];
        manifest.ImportPolicy = importPolicy;

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.ValidateProtectedQuestionProjectImportContract(
                source,
                manifest,
                FollowUpSchemaDecisionProcessor.MapSchema(source, null),
                FollowUpSchemaDecisionProcessor.MapManifest(manifest, null)));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("Upsert", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("form_project", "hospital_id")]
    [InlineData("form_question", "hospital_id")]
    [InlineData("form_question", "project_id")]
    [InlineData("form_question", "table_name")]
    [InlineData("form_question", "column_name")]
    [InlineData("form_question", "data_type")]
    public void 项目题目安全字段不可写时不得静默过滤(string tableName, string unavailableColumn)
    {
        var manifest = CreateManifest($"form_{tableName}.jsonl", 1, "form", tableName);
        var writable = FollowUpPackageSchemaCheckService
            .GetProtectedQuestionProjectRequiredColumns("form", tableName)
            .Where(column => !column.Equals(unavailableColumn, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageImportService.EnsureProtectedQuestionProjectWritableColumns(
                manifest,
                writable));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains(unavailableColumn, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Upsert")]
    [InlineData("UseExistingById")]
    [InlineData("RejectIfMissing")]
    public void 项目写入前归属校验不受ImportPolicy影响(string importPolicy)
    {
        var source = CreateManifest("form_form_project.jsonl", 1, "form", "form_project");
        source.ImportPolicy = importPolicy;
        var method = typeof(FollowUpPackageImportService).GetMethod(
            "RequiresPackageProjectPrewriteValidation",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True(Assert.IsType<bool>(method.Invoke(null, [source])));
    }

    [Fact]
    public void Package未覆盖项目在无动态表时也必须在题目写入前复验()
    {
        var method = typeof(FollowUpPackageImportService).GetMethod(
            "ShouldVerifyPackageQuestionProjectScope",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True(Assert.IsType<bool>(method.Invoke(
            null,
            [false, "form", "form_question", false])));
        Assert.True(Assert.IsType<bool>(method.Invoke(
            null,
            [false, "target", "form_answers", true])));
        Assert.False(Assert.IsType<bool>(method.Invoke(
            null,
            [false, "form", "form_project", false])));
        Assert.False(Assert.IsType<bool>(method.Invoke(
            null,
            [true, "form", "form_question", false])));
    }

    [Theory]
    [InlineData("UseExistingById", "SELECT")]
    [InlineData("RejectIfMissing", "SELECT")]
    [InlineData("InsertIfMissing", "INSERT")]
    [InlineData("Upsert", "INSERT", "UPDATE", "SELECT")]
    public void 导入策略只要求执行所需的最小字段权限(
        string importPolicy,
        params string[] expectedPrivileges)
    {
        var actualPrivileges = FollowUpImportPolicyPermissions.GetRequiredColumnPrivileges(importPolicy);
        var predicate = FollowUpImportPolicyPermissions.BuildColumnPrivilegePredicate(importPolicy);

        Assert.Equal(expectedPrivileges, actualPrivileges);
        foreach (var privilege in expectedPrivileges)
            Assert.Contains($"'{privilege}'", predicate, StringComparison.Ordinal);
        foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE" }.Except(expectedPrivileges))
            Assert.DoesNotContain($"'{privilege}'", predicate, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("form_form_question.jsonl")]
    [InlineData("form_form_project.jsonl")]
    public async Task 导入快照校验hash后不受原文件替换影响(string fileName)
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-import-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, fileName);
            var original = Encoding.UTF8.GetBytes("{\"id\":\"original\"}\n");
            await File.WriteAllBytesAsync(sourcePath, original);
            var expectedHash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
            var method = typeof(FollowUpPackageImportService).GetMethod(
                "OpenVerifiedImportSnapshotAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var task = Assert.IsAssignableFrom<Task<FileStream>>(method.Invoke(
                null,
                [sourcePath, expectedHash, CancellationToken.None]));
            await using var snapshot = await task;

            var replacementPath = Path.Combine(root, "replacement.jsonl");
            await File.WriteAllTextAsync(replacementPath, "{\"id\":\"replacement\"}\n", new UTF8Encoding(false));
            File.Delete(sourcePath);
            File.Move(replacementPath, sourcePath);
            using var memory = new MemoryStream();
            await snapshot.CopyToAsync(memory);

            Assert.Equal(original, memory.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("form_form_question.jsonl")]
    [InlineData("form_form_project.jsonl")]
    public async Task 导入快照实际字节hash不符时必须阻断(string fileName)
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-import-snapshot-hash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, fileName);
            var original = Encoding.UTF8.GetBytes("{\"id\":\"original\"}\n");
            await File.WriteAllBytesAsync(sourcePath, Encoding.UTF8.GetBytes("{\"id\":\"replacement\"}\n"));
            var expectedHash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
            var method = typeof(FollowUpPackageImportService).GetMethod(
                "OpenVerifiedImportSnapshotAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var task = Assert.IsAssignableFrom<Task<FileStream>>(method.Invoke(
                null,
                [sourcePath, expectedHash, CancellationToken.None]));

            var exception = await Assert.ThrowsAsync<InvalidDataException>(async () => await task);

            Assert.Contains("hash", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 目标表单项快照SQL只按源结构列投影后计算hash()
    {
        Assert.Contains("LEFT JOIN form.form_project", FollowUpPackageSchemaCheckService.TargetQuestionScopeSnapshotSql,
            StringComparison.Ordinal);
        Assert.Contains("jsonb_each(to_jsonb(question))", FollowUpPackageSchemaCheckService.TargetQuestionScopeSnapshotSql,
            StringComparison.Ordinal);
        Assert.Contains("property.key = ANY(@sourceColumns)", FollowUpPackageSchemaCheckService.TargetQuestionScopeSnapshotSql,
            StringComparison.Ordinal);
        Assert.Contains("jsonb_object_agg", FollowUpPackageSchemaCheckService.TargetQuestionScopeSnapshotSql,
            StringComparison.Ordinal);
        Assert.Contains("ORDER BY projected.row_json", FollowUpPackageSchemaCheckService.TargetQuestionScopeSnapshotSql,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public void 仅附件开始变更且事务未提交时执行恢复(
        bool importCommitted,
        bool attachmentMutationStarted,
        bool shouldRestore)
    {
        Assert.Equal(
            shouldRestore,
            FollowUpPackageImportService.ShouldRestoreAttachments(importCommitted, attachmentMutationStarted));
    }

    [Fact]
    public void 事务内表单项漂移会生成待结构处理结果()
    {
        var result = FollowUpPackageImportService.CreateSchemaReviewFailureResult(
            new FollowUpPackageException(
                FollowUpErrorCodes.SchemaReviewRequired,
                "医院端 form.form_question 实际内容 hash 与数据包不一致。"));

        Assert.False(result.Compatible);
        Assert.Equal("ReviewRequired", result.Status);
        Assert.Equal("RequiresMapping", result.DiffLevel);
        Assert.Contains(result.Messages, message => message.Contains("实际内容 hash", StringComparison.Ordinal));
    }

    [Fact]
    public void 数据包医院标识必须与LHYY配置一致()
    {
        var hospitalId = Guid.NewGuid();
        var manifest = new FollowUpPackageManifest { HospitalId = hospitalId, HospitalCode = "H001" };
        var options = new FollowUpPackageImportOptions
        {
            HospitalId = hospitalId.ToString("D"),
            HospitalCode = "H001"
        };

        FollowUpPackageImportService.ValidateHospitalIdentity(manifest, options);

        options.HospitalId = Guid.NewGuid().ToString("D");
        Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageImportService.ValidateHospitalIdentity(manifest, options));
    }

    [Fact]
    public void 等待结构处理时不发送终态失败ACK()
    {
        Assert.Null(FollowUpPackageImportService.ResolveFailureAckStatus("WaitingForDecision"));
        Assert.Equal("ImportFailed", FollowUpPackageImportService.ResolveFailureAckStatus("ImportFailed"));
        Assert.Equal("ImportFailed", FollowUpPackageImportService.ResolveFailureAckStatus("RestoreFailed"));
    }

    [Fact]
    public void 结构错误摘要按类别计数并只展示代表项()
    {
        var messages = Enumerable.Range(1, 12)
            .Select(index => $"目标字段不存在：target.answers.field_{index}")
            .Append("字段类型不兼容：target.answers.file（ARRAY → text）")
            .ToList();

        var summary = FollowUpPackageImportService.FormatSchemaCheckSummary(messages);

        Assert.Contains("目标字段不存在 12 项", summary, StringComparison.Ordinal);
        Assert.Contains("字段类型不兼容 1 项", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("field_12", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void 空表与有数据表混合时只校验实际导入表()
    {
        var result = FollowUpPackageSchemaCheckService.Evaluate(
            [
                CreateTable("uuid", false),
                CreateTable("uuid", false, "system", "sys_hospital")
            ],
            [
                CreateTable("integer", false),
                CreateTable("uuid", false, "system", "sys_hospital")
            ],
            [
                CreateManifest(exportPath: null, recordCount: 0),
                CreateManifest("data/system_sys_hospital.jsonl", 1, "system", "sys_hospital")
            ]);

        Assert.True(result.Compatible);
        Assert.Equal("Compatible", result.DiffLevel);
    }

    [Fact]
    public void 表映射碰撞时不会重新带入无导出文件的空表()
    {
        var decision = new FollowUpSchemaDecision
        {
            DecisionStatus = "ApprovedMapping",
            TableMappings =
            {
                ["legacy.patient"] = new FollowUpTableMapping
                {
                    TargetSchema = "care",
                    TargetTable = "patient"
                }
            }
        };
        var manifest = new[]
        {
            CreateManifest(exportPath: null, recordCount: 0, "legacy", "patient"),
            CreateManifest("data/care_patient.jsonl", 1)
        };

        var sources = FollowUpPackageSchemaCheckService.SelectAndMapSourceTables(
            [
                CreateTable("integer", false, "legacy", "patient"),
                CreateTable("uuid", false)
            ],
            manifest,
            decision);
        var mappedManifest = manifest
            .Select(item => FollowUpSchemaDecisionProcessor.MapManifest(item, decision))
            .ToList();
        var result = FollowUpPackageSchemaCheckService.Evaluate(
            sources,
            [CreateTable("uuid", false)],
            mappedManifest);

        Assert.True(result.Compatible);
        Assert.Single(sources);
    }

    [Fact]
    public void 多个源表映射同一目标表时返回Breaking而非内部异常()
    {
        var decision = new FollowUpSchemaDecision
        {
            DecisionStatus = "ApprovedMapping",
            TableMappings =
            {
                ["legacy.first_patient"] = new FollowUpTableMapping
                {
                    TargetSchema = "care",
                    TargetTable = "patient"
                },
                ["legacy.second_patient"] = new FollowUpTableMapping
                {
                    TargetSchema = "care",
                    TargetTable = "patient"
                }
            }
        };
        var manifest = new[]
        {
            CreateManifest("data/first.jsonl", 1, "legacy", "first_patient"),
            CreateManifest("data/second.jsonl", 1, "legacy", "second_patient")
        };
        var sources = FollowUpPackageSchemaCheckService.SelectAndMapSourceTables(
            [
                CreateTable("uuid", false, "legacy", "first_patient"),
                CreateTable("uuid", false, "legacy", "second_patient")
            ],
            manifest,
            decision);
        var mappedManifest = manifest
            .Select(item => FollowUpSchemaDecisionProcessor.MapManifest(item, decision))
            .ToList();
        var target = CreateTable("uuid", false);

        var result = FollowUpPackageSchemaCheckService.Evaluate(
            sources,
            [target, target],
            mappedManifest);

        Assert.False(result.Compatible);
        Assert.Equal("Breaking", result.DiffLevel);
        Assert.Contains(result.Messages, message =>
            message.Contains("重复", StringComparison.Ordinal));
    }

    [Fact]
    public void 动态源结构存在大小写重复字段时返回Breaking而非内部异常()
    {
        var source = CreateDynamicTable(
            ("duplicate_question", "text"),
            ("Duplicate_Question", "text"));
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");

        var build = FollowUpPackageSchemaCheckService.BuildDynamicColumnScopeDefinitions(
            new FollowUpSchemaSnapshot { Tables = [source] },
            [manifest],
            [new FollowUpQuestionReference("form_answers", "duplicate_question", "文本")],
            FollowUpQuestionScopeSource.Package,
            decision: null);

        Assert.Contains(build.BreakingMessages, message =>
            message.Contains("重复字段", StringComparison.Ordinal));
    }

    [Fact]
    public void 动态源结构存在大小写重复表时映射前要求结构复核()
    {
        var exact = CreateDynamicTable(("active_question", "text"));
        var caseVariant = CreateDynamicTable(("active_question", "text"));
        caseVariant.TableName = "Form_Answers";
        var manifest = CreateManifest(
            "data/target_form_answers.jsonl",
            1,
            "target",
            "form_answers",
            "DynamicFormData");

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.SelectAndMapSourceTables(
                [exact, caseVariant],
                [manifest],
                decision: null,
                [CreateDynamicScope("active_question")]));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("重复", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 完全重复的动态表清单项直接要求结构复核()
    {
        var manifests = new[]
        {
            CreateManifest(
                "data/target_form_answers.jsonl",
                1,
                "target",
                "form_answers",
                "DynamicFormData"),
            CreateManifest(
                "data/target_form_answers.jsonl",
                1,
                "target",
                "form_answers",
                "DynamicFormData")
        };

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageSchemaCheckService.BuildDynamicColumnScopeDefinitions(
                new FollowUpSchemaSnapshot { Tables = [CreateDynamicTable()] },
                manifests,
                [],
                FollowUpQuestionScopeSource.Package,
                decision: null));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("重复", exception.Message, StringComparison.Ordinal);
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

    [Fact]
    public void 表单卡片按父记录优先排序()
    {
        const string parentId = "11111111-1111-1111-1111-111111111111";
        const string childId = "22222222-2222-2222-2222-222222222222";
        var rows = new[]
        {
            $$"""{"id":"{{childId}}","parent_id":"{{parentId}}"}""",
            $$"""{"id":"{{parentId}}","parent_id":null}"""
        };

        var ordered = FollowUpImportRowOrdering.Order("form", "form_card", rows);

        Assert.Contains(parentId, ordered[0]);
        Assert.Contains(childId, ordered[1]);
    }

    [Fact]
    public void 表单卡片循环父子关系被拒绝()
    {
        const string firstId = "11111111-1111-1111-1111-111111111111";
        const string secondId = "22222222-2222-2222-2222-222222222222";
        var rows = new[]
        {
            $$"""{"id":"{{firstId}}","parent_id":"{{secondId}}"}""",
            $$"""{"id":"{{secondId}}","parent_id":"{{firstId}}"}"""
        };

        var exception = Assert.Throws<InvalidDataException>(() =>
            FollowUpImportRowOrdering.Order("form", "form_card", rows));

        Assert.Contains("循环父子关系", exception.Message);
    }

    private static FollowUpTableManifestItem CreateManifest(
        string? exportPath,
        int recordCount,
        string schema = "care",
        string tableName = "patient",
        string dataCategory = "BusinessData") => new()
    {
        Schema = schema,
        TableName = tableName,
        Enabled = true,
        ExportPath = exportPath,
        RecordCount = recordCount,
        DataCategory = dataCategory
    };

    private static async Task<FollowUpVerifiedPackage> CreateQuestionPackageAsync(
        string stagingPath,
        Guid hospitalId,
        IReadOnlyList<string> questionRows,
        IReadOnlyList<string> projectRows)
    {
        const string questionExportPath = "form_form_question.jsonl";
        const string projectExportPath = "form_form_project.jsonl";
        var questionPath = Path.Combine(stagingPath, questionExportPath);
        var projectPath = Path.Combine(stagingPath, projectExportPath);
        await File.WriteAllLinesAsync(questionPath, questionRows, new UTF8Encoding(false));
        await File.WriteAllLinesAsync(projectPath, projectRows, new UTF8Encoding(false));
        var questionHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(questionPath)))
            .ToLowerInvariant();
        var projectHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(projectPath)))
            .ToLowerInvariant();
        var questionItem = CreateManifest(
            questionExportPath,
            questionRows.Count,
            "form",
            "form_question",
            "Relationship");
        questionItem.Required = true;
        questionItem.HasIncrementalData = questionRows.Count > 0;
        questionItem.FileHash = questionHash;
        questionItem.ContentHash = questionHash;
        var projectItem = CreateManifest(
            projectExportPath,
            projectRows.Count,
            "form",
            "form_project",
            "Relationship");
        projectItem.Required = true;
        projectItem.HasIncrementalData = projectRows.Count > 0;
        projectItem.FileHash = projectHash;
        projectItem.ContentHash = projectHash;
        return new FollowUpVerifiedPackage(
            PackagePath: string.Empty,
            PackageHash: string.Empty,
            StagingPath: stagingPath,
            Envelope: new FollowUpEncryptedEnvelope(),
            Manifest: new FollowUpPackageManifest
            {
                HospitalId = hospitalId,
                PackageType = "Incremental",
                DataFiles =
                [
                    new FollowUpDataFileManifest
                    {
                        Path = questionExportPath,
                        Table = "form.form_question",
                        Hash = questionHash,
                        RecordCount = questionRows.Count
                    },
                    new FollowUpDataFileManifest
                    {
                        Path = projectExportPath,
                        Table = "form.form_project",
                        Hash = projectHash,
                        RecordCount = projectRows.Count
                    }
                ]
            },
            TableManifest: [questionItem, projectItem],
            SchemaSnapshot: new FollowUpSchemaSnapshot
            {
                Tables =
                [
                    new FollowUpTableSchema
                    {
                        SchemaName = "form",
                        TableName = "form_question",
                        Columns = new[] { "id", "hospital_id", "project_id", "table_name", "column_name", "data_type" }
                            .Select((name, index) => new FollowUpColumnSchema
                            {
                                Name = name,
                                DataType = "text",
                                IsNullable = true,
                                OrdinalPosition = index + 1
                            })
                            .ToList()
                    }
                ]
            },
            SchemaDiff: new FollowUpSchemaDiff());
    }

    private static string? ReadNullableString(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static FollowUpTableColumnScope CreateDynamicScope(params string[] questionColumns) => new(
        "target",
        "form_answers",
        FollowUpPackageSchemaCheckService.DynamicFixedColumns
            .Concat(questionColumns)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList());

    private static FollowUpTableSchema CreateDynamicTable(params (string Name, string Type)[] questionColumns)
    {
        var columns = FollowUpPackageSchemaCheckService.DynamicFixedColumns
            .Select((name, index) => new FollowUpColumnSchema
            {
                Name = name,
                DataType = name == "id" ? "uuid" : "text",
                IsNullable = name != "id",
                DefaultValue = name == "id" ? "gen_random_uuid()" : null,
                OrdinalPosition = index + 1
            })
            .ToList();
        columns.AddRange(questionColumns.Select((item, index) => new FollowUpColumnSchema
        {
            Name = item.Name,
            DataType = item.Type,
            IsNullable = true,
            OrdinalPosition = columns.Count + index + 1
        }));
        return new FollowUpTableSchema
        {
            SchemaName = "target",
            TableName = "form_answers",
            PrimaryKey = ["id"],
            Columns = columns
        };
    }

    private static FollowUpTableSchema CreateTable(
        string idType,
        bool nullable,
        string schema = "care",
        string tableName = "patient") => new()
    {
        SchemaName = schema,
        TableName = tableName,
        PrimaryKey = ["id"],
        Columns =
        [
            new FollowUpColumnSchema { Name = "id", DataType = idType, IsNullable = nullable, OrdinalPosition = 1 },
            new FollowUpColumnSchema { Name = "name", DataType = "text", IsNullable = true, OrdinalPosition = 2 }
        ]
    };

    private static FollowUpTableSchema CreateProtectedTableSchema(string tableName)
    {
        var columns = tableName == "form_project"
            ? new[] { "id", "hospital_id" }
            : new[] { "id", "hospital_id", "project_id", "table_name", "column_name", "data_type" };
        return new FollowUpTableSchema
        {
            SchemaName = "form",
            TableName = tableName,
            PrimaryKey = ["id"],
            Columns = columns.Select((name, index) => new FollowUpColumnSchema
            {
                Name = name,
                DataType = name.EndsWith("_id", StringComparison.Ordinal) || name == "id" ? "uuid" : "text",
                IsNullable = false,
                OrdinalPosition = index + 1
            }).ToList()
        };
    }
}
