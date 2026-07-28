using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using DataSync.LHYY.V2.Services.FollowUp;
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
    }

    [Fact]
    public void 目标表单项范围可反向解析已批准字段映射()
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

        var scope = Assert.Single(build.Scopes);
        Assert.Empty(build.BreakingMessages);
        Assert.Contains("source_file", scope.SourceColumns);
        Assert.Contains("hospital_file", scope.TargetColumns);
        Assert.Equal(["source_file"], scope.ArrayToTextSourceColumns);
        Assert.Equal(["hospital_file"], scope.ArrayToTextTargetColumns);
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
}
