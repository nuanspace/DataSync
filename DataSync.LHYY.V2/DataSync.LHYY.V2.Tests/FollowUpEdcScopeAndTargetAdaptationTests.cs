using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using DataSync.LHYY.V2.Services.FollowUp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class FollowUpEdcScopeAndTargetAdaptationTests
{
    [Fact]
    public void EDC权限映射SQL限定本包患者且保持幂等()
    {
        var sql = FollowUpEdcScopeService.BuildUpsertSql();

        Assert.Contains("p.id = ANY(@patient_ids)", sql);
        Assert.Contains("pe.project_id = ANY(@edc_project_ids)", sql);
        Assert.Contains("datasync.followup_patient_source_map", sql);
        Assert.Contains("p.project_id", sql);
        Assert.Contains("UNION", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("p.source_type = 'followup'", sql);
        Assert.Contains("fs.type = 'edc'", sql);
        Assert.Contains("scope_map.patient_id = desired.patient_id", sql);
        Assert.Contains("scope_map.project_id = desired.project_id", sql);
        Assert.Contains("md5(candidate.patient_id::text || ':' || candidate.project_id::text)::uuid", sql);
        Assert.Contains("UPDATE public.patient_data_scope_map", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("department_id = desired.department_id", sql);
        Assert.Contains("ward_id = desired.ward_id", sql);
    }

    [Theory]
    [InlineData("followup")]
    [InlineData("care")]
    [InlineData("wechat")]
    [InlineData(null)]
    public void 所有回传患者均适配为Care并保留原始来源(string? originalSourceType)
    {
        var patientId = Guid.NewGuid();
        var sourceTypeJson = originalSourceType is null ? "null" : $"\"{originalSourceType}\"";
        var source = $"{{\"id\":\"{patientId}\",\"source_type\":{sourceTypeJson},\"name\":\"患者\"}}";

        var patientSource = FollowUpTargetAdaptationService.ReadPatientSource("public", "patient", source);
        var adapted = FollowUpTargetAdaptationService.AdaptRow("public", "patient", source);

        Assert.NotNull(patientSource);
        Assert.Equal(patientId, patientSource.PatientId);
        Assert.Equal(originalSourceType, patientSource.OriginalSourceType);
        using var document = JsonDocument.Parse(adapted);
        Assert.Equal("care", document.RootElement.GetProperty("source_type").GetString());
    }

    [Theory]
    [InlineData("随访", "已审核", null)]
    [InlineData("随访", "未到推送时间", null)]
    [InlineData("预问诊", "门诊结束", "2026-07-22T08:00:00")]
    [InlineData("转诊记录", "待确认", "2026-07-22T08:00:00")]
    public void 患者事件已由云端筛选且医院端保持原始字段(string eventType, string status, string? inputTime)
    {
        var source = PatientEventJson(eventType, status, inputTime);

        var adapted = FollowUpTargetAdaptationService.AdaptRow("care", "patient_event", source);

        using var document = JsonDocument.Parse(adapted);
        Assert.Equal(eventType, document.RootElement.GetProperty("event_type").GetString());
        Assert.Equal(status, document.RootElement.GetProperty("event_status").GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", document.RootElement.GetProperty("form_set_id").GetString());
        Assert.Equal("测试表单", document.RootElement.GetProperty("form_set_name").GetString());
        Assert.Equal("22222222-2222-2222-2222-222222222222", document.RootElement.GetProperty("event_type_definition_id").GetString());
    }

    [Fact]
    public void 无表单住院基础事件补齐目标库表单链接()
    {
        const string source = """
            {"id":"33333333-3333-3333-3333-333333333333","project_id":"44444444-4444-4444-4444-444444444444","event_type":"住院","form_set_id":null,"form_set_name":null,"event_type_definition_id":null}
            """;
        var mapping = new FollowUpPatientEventFormMapping(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            "住院信息");

        var adapted = FollowUpTargetAdaptationService.ApplyPatientEventFormMapping(source, mapping);

        using var document = JsonDocument.Parse(adapted);
        Assert.Equal("55555555-5555-5555-5555-555555555555", document.RootElement.GetProperty("event_type_definition_id").GetString());
        Assert.Equal("66666666-6666-6666-6666-666666666666", document.RootElement.GetProperty("form_set_id").GetString());
        Assert.Equal("住院信息", document.RootElement.GetProperty("form_set_name").GetString());
    }

    [Fact]
    public async Task 已有表单患者事件不查询目标映射且保持原始字段()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CubeDb"] = "Host=unused;Database=unused;Username=unused;Password=unused"
            })
            .Build();
        var service = new FollowUpTargetAdaptationService(configuration);
        var source = PatientEventJson("随访", "已审核", null);

        var adapted = await service.AdaptRowAsync(
            null!,
            null!,
            "care",
            "patient_event",
            source,
            new Dictionary<Guid, string>(),
            new Dictionary<(Guid ProjectId, string EventType), IReadOnlyList<FollowUpPatientEventFormMapping>>(),
            CancellationToken.None);

        Assert.Equal(source, adapted);
    }

    [Fact]
    public void 无表单基础事件缺少目标映射时阻断导入()
    {
        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpTargetAdaptationService.SelectPatientEventFormMapping(
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                "住院",
                []));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("住院", exception.Message);
    }

    [Fact]
    public void 无表单基础事件存在多个目标映射时阻断导入()
    {
        var mappings = new[]
        {
            new FollowUpPatientEventFormMapping(Guid.NewGuid(), Guid.NewGuid(), "住院信息一"),
            new FollowUpPatientEventFormMapping(Guid.NewGuid(), Guid.NewGuid(), "住院信息二")
        };

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpTargetAdaptationService.SelectPatientEventFormMapping(
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                "住院",
                mappings));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("多个", exception.Message);
    }

    [Theory]
    [InlineData("住院")]
    [InlineData("门诊")]
    public void 仅允许包内有关联明细的住院门诊基础事件(string eventType)
    {
        var eventId = Guid.NewGuid();

            FollowUpTargetAdaptationService.EnsureSupportedBasePatientEvent(
            eventId,
            eventType,
            new Dictionary<Guid, string> { [eventId] = eventType });
    }

    [Fact]
    public void 无关联明细的无表单事件阻断导入()
    {
        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpTargetAdaptationService.EnsureSupportedBasePatientEvent(
                Guid.NewGuid(),
                "住院",
                new Dictionary<Guid, string>()));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("住院/门诊明细", exception.Message);
    }

    [Fact]
    public void 非住院门诊无表单事件阻断导入()
    {
        var eventId = Guid.NewGuid();
        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpTargetAdaptationService.EnsureSupportedBasePatientEvent(
                eventId,
                "随访",
                new Dictionary<Guid, string> { [eventId] = "住院" }));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("仅支持住院或门诊", exception.Message);
    }

    [Fact]
    public void 住院事件关联门诊明细时阻断导入()
    {
        var eventId = Guid.NewGuid();
        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpTargetAdaptationService.EnsureSupportedBasePatientEvent(
                eventId,
                "住院",
                new Dictionary<Guid, string> { [eventId] = "门诊" }));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("关联类型不一致", exception.Message);
    }

    [Fact]
    public void 同一基础事件同时关联住院和门诊明细时阻断导入()
    {
        var eventId = Guid.NewGuid();
        var associations = new Dictionary<Guid, string>();
        FollowUpPackageImportService.AddBasePatientEventAssociation(associations, eventId, "住院");

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPackageImportService.AddBasePatientEventAssociation(associations, eventId, "门诊"));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("同时关联", exception.Message);
    }

    [Fact]
    public void 基础事件关联批量查询同时覆盖住院和门诊目标表()
    {
        var sql = FollowUpPackageImportService.BuildExistingBasePatientEventTypesSql();

        Assert.Contains("care.patient_hospitalized", sql);
        Assert.Contains("care.patient_outpatient", sql);
        Assert.Contains("ANY(@event_ids)", sql);
        Assert.Contains("UNION ALL", sql);
    }

    [Fact]
    public async Task 同一项目和事件类型在单次导入事务内只查询一次映射()
    {
        var projectId = Guid.NewGuid();
        var calls = 0;
        var mapping = new FollowUpPatientEventFormMapping(Guid.NewGuid(), Guid.NewGuid(), "住院信息");
        var cache = new Dictionary<(Guid ProjectId, string EventType), IReadOnlyList<FollowUpPatientEventFormMapping>>();

        async Task<IReadOnlyList<FollowUpPatientEventFormMapping>> LoadAsync()
        {
            calls++;
            await Task.Yield();
            return [mapping];
        }

        var first = await FollowUpTargetAdaptationService.GetPatientEventFormMappingsAsync(
            cache, projectId, "住院", LoadAsync);
        var second = await FollowUpTargetAdaptationService.GetPatientEventFormMappingsAsync(
            cache, projectId, "住院", LoadAsync);

        Assert.Same(first, second);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void 非空非法表单标识阻断导入(string formSetId)
    {
        var source = $$"""
            {"id":"33333333-3333-3333-3333-333333333333","project_id":"44444444-4444-4444-4444-444444444444","event_type":"住院","form_set_id":"{{formSetId}}"}
            """;

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpTargetAdaptationService.ReadOptionalGuid(source, "form_set_id", "患者事件"));

        Assert.Equal(FollowUpErrorCodes.SchemaReviewRequired, exception.ErrorCode);
        Assert.Contains("form_set_id", exception.Message);
    }

    [Fact]
    public void 患者导入目标库时将FollowUp来源适配为Care且保留其他原始字段()
    {
        var patientId = Guid.NewGuid();
        var source = $$"""
            {"id":"{{patientId}}","source_type":"followup","name":"张三","is_valid":true}
            """;

        var adapted = FollowUpTargetAdaptationService.AdaptRow("public", "patient", source);

        using var document = JsonDocument.Parse(adapted);
        Assert.Equal(patientId.ToString(), document.RootElement.GetProperty("id").GetString());
        Assert.Equal("care", document.RootElement.GetProperty("source_type").GetString());
        Assert.Equal("张三", document.RootElement.GetProperty("name").GetString());
        Assert.True(document.RootElement.GetProperty("is_valid").GetBoolean());
    }

    [Fact]
    public void 非患者表不做目标适配()
    {
        const string source = "{\"source_type\":\"followup\",\"name\":\"原值\"}";

        var adapted = FollowUpTargetAdaptationService.AdaptRow("care", "patient_outpatient", source);

        Assert.Equal(source, adapted);
    }

    [Fact]
    public void 来源映射SQL按患者主键幂等记录包与医院信息()
    {
        var sql = FollowUpTargetAdaptationService.BuildSourceMapUpsertSql();

        Assert.Contains("INSERT INTO datasync.followup_patient_source_map", sql);
        Assert.Contains("ON CONFLICT (patient_id) DO UPDATE", sql);
        Assert.Contains("@hospital_code", sql);
        Assert.Contains("@package_id", sql);
        Assert.Contains("original_source_type", sql);
    }

    [Fact]
    public void V3导入契约默认值正确且传输协议保持V1()
    {
        var options = new FollowUpPackageImportOptions();
        var request = FollowUpRelayRequest.Create("list", "token", new { });

        Assert.Equal("followup-hospital-sync.v3", options.SupportedContractVersion);
        Assert.Equal("1.2.0", options.ImporterVersion);
        Assert.Equal("1.0", request.ProtocolVersion);
    }

    [Fact]
    public void V3导入器明确拒绝旧V2数据包()
    {
        var options = new FollowUpPackageImportOptions();
        var manifest = new FollowUpPackageManifest
        {
            ExportContractVersion = "followup-hospital-sync.v2",
            MinImporterVersion = "1.1.0"
        };

        var exception = Assert.Throws<FollowUpPackageException>(
            () => FollowUpPackageImportService.ValidateVersions(manifest, options));

        Assert.Equal(FollowUpErrorCodes.ContractVersionUnsupported, exception.ErrorCode);
    }

    [Fact]
    public void 即使遗留配置声明支持V2也必须拒绝旧V2数据包()
    {
        var options = new FollowUpPackageImportOptions
        {
            SupportedContractVersion = "followup-hospital-sync.v2",
            ImporterVersion = "1.1.0"
        };
        var manifest = new FollowUpPackageManifest
        {
            ExportContractVersion = "followup-hospital-sync.v2",
            MinImporterVersion = "1.1.0"
        };

        var exception = Assert.Throws<FollowUpPackageException>(
            () => FollowUpPackageImportService.ValidateVersions(manifest, options));

        Assert.Equal(FollowUpErrorCodes.ContractVersionUnsupported, exception.ErrorCode);
    }

    [Fact]
    public void V3导入器接受V3数据包()
    {
        var options = new FollowUpPackageImportOptions();
        var manifest = new FollowUpPackageManifest
        {
            ExportContractVersion = "followup-hospital-sync.v3",
            MinImporterVersion = "1.2.0"
        };

        FollowUpPackageImportService.ValidateVersions(manifest, options);
    }

    [Fact]
    public void 配置不得冒充高于当前二进制的导入器版本()
    {
        var options = new FollowUpPackageImportOptions
        {
            ImporterVersion = "1.3.0"
        };
        var manifest = new FollowUpPackageManifest
        {
            ExportContractVersion = "followup-hospital-sync.v3",
            MinImporterVersion = "1.3.0"
        };

        var exception = Assert.Throws<FollowUpPackageException>(
            () => FollowUpPackageImportService.ValidateVersions(manifest, options));

        Assert.Equal(FollowUpErrorCodes.ContractVersionUnsupported, exception.ErrorCode);
    }

    [Fact]
    public void 导入审计仓储从统一配置读取ImporterVersion()
    {
        var constructor = Assert.Single(typeof(FollowUpPackageImportRepository).GetConstructors());

        Assert.Contains(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(IOptions<FollowUpPackageImportOptions>));
    }

    [Fact]
    public void EDC权限映射表缺关键列时预检失败()
    {
        var columns = new[] { "patient_id", "hospital_id", "project_id" };

        var missing = FollowUpEdcScopeService.GetMissingRequiredColumns(columns);

        Assert.Equal(["created_time", "department_id", "id", "ward_id"], missing.OrderBy(x => x));
    }

    [Fact]
    public async Task 非EDC包不会访问权限映射表()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CubeDb"] = "Host=unused;Database=unused;Username=unused;Password=unused"
            })
            .Build();
        var service = new FollowUpEdcScopeService(configuration);
        var plan = new FollowUpEdcScopePlan([Guid.NewGuid()], [], ShouldApply: false);

        var count = await service.ApplyAsync(null!, null!, plan, CancellationToken.None);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task EDC范围读取在数据文件hash不符时阻断()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-edc-hash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const string relativePath = "data/public_patient.jsonl";
            var filePath = Path.Combine(root, "data", "public_patient.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllTextAsync(filePath, "{}\n", new UTF8Encoding(false));
            var package = CreateVerifiedPackage(root, relativePath, new string('0', 64));
            var service = CreateEdcScopeService();

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.PrepareAsync(package, null, CancellationToken.None));

            Assert.Contains("hash", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EDC范围读取在清单生成后路径被替换时阻断()
    {
        var root = Path.Combine(Path.GetTempPath(), $"followup-edc-replace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const string relativePath = "data/public_patient.jsonl";
            var filePath = Path.Combine(root, "data", "public_patient.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var original = Encoding.UTF8.GetBytes("{}\n");
            await File.WriteAllBytesAsync(filePath, original);
            var expectedHash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
            var package = CreateVerifiedPackage(root, relativePath, expectedHash);

            var replacementPath = Path.Combine(root, "replacement.jsonl");
            await File.WriteAllTextAsync(replacementPath, "{\"name\":\"replacement\"}\n", new UTF8Encoding(false));
            File.Move(replacementPath, filePath, overwrite: true);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                CreateEdcScopeService().PrepareAsync(package, null, CancellationToken.None));

            Assert.Contains("hash", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 仅含小写EDC表单集的增量也生成项目级权限计划()
    {
        var projectId = Guid.NewGuid();
        var scope = FollowUpEdcScopeService.AnalyzeRows(
            "form",
            "form_form_set",
            [$"{{\"project_id\":\"{projectId}\",\"type\":\"edc\"}}"]);

        var plan = FollowUpEdcScopeService.CreatePlan(scope, hasTargetEdcData: false);

        Assert.True(plan.ShouldApply);
        Assert.Empty(plan.PatientIds);
        Assert.Equal([projectId], plan.EdcProjectIds);
    }

    [Fact]
    public void 大写EDC不按EDC项目处理()
    {
        var projectId = Guid.NewGuid();
        var scope = FollowUpEdcScopeService.AnalyzeRows(
            "form",
            "form_form_set",
            [$"{{\"project_id\":\"{projectId}\",\"type\":\"EDC\"}}"]);

        var plan = FollowUpEdcScopeService.CreatePlan(scope, hasTargetEdcData: false);

        Assert.False(plan.ShouldApply);
        Assert.Empty(plan.EdcProjectIds);
    }

    [Fact]
    public void FormProject增量会触发现有EDC权限映射刷新()
    {
        var projectId = Guid.NewGuid();
        var row = JsonSerializer.Serialize(new { id = projectId, department_id = Guid.NewGuid() });
        var scope = FollowUpEdcScopeService.AnalyzeRows(
            "form",
            "form_project",
            [row]);

        var plan = FollowUpEdcScopeService.CreatePlan(scope, hasTargetEdcData: true);

        Assert.True(plan.ShouldApply);
        Assert.Empty(plan.PatientIds);
        Assert.Equal([projectId], plan.EdcProjectIds);
    }

    [Fact]
    public async Task 数据提交后的最终化异常不会触发附件恢复()
    {
        var restored = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FollowUpPackageImportService.ExecuteCommitBoundaryAsync(
                () => Task.FromResult(1),
                _ =>
                {
                    restored = true;
                    return Task.CompletedTask;
                },
                _ => Task.FromException<int>(new InvalidOperationException("最终化失败"))));

        Assert.False(restored);
    }

    [Fact]
    public async Task 数据提交后资源释放异常不会触发附件恢复()
    {
        var committed = false;
        var restored = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FollowUpPackageImportService.ExecuteCommitBoundaryAsync<int, int>(
                () =>
                {
                    committed = true;
                    return Task.FromException<int>(new InvalidOperationException("提交后的资源释放失败"));
                },
                _ =>
                {
                    if (!committed)
                        restored = true;
                    return Task.CompletedTask;
                },
                Task.FromResult));

        Assert.True(committed);
        Assert.False(restored);
    }

    [Fact]
    public async Task 数据提交前异常仍会触发附件恢复()
    {
        var restored = false;
        var finalized = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FollowUpPackageImportService.ExecuteCommitBoundaryAsync<int, int>(
                () => Task.FromException<int>(new InvalidOperationException("事务失败")),
                _ =>
                {
                    restored = true;
                    return Task.CompletedTask;
                },
                value =>
                {
                    finalized = true;
                    return Task.FromResult(value);
                }));

        Assert.True(restored);
        Assert.False(finalized);
    }

    private static string PatientEventJson(string eventType, string status, string? inputTime)
    {
        var inputTimeJson = inputTime is null ? "null" : $"\"{inputTime}\"";
        return $$"""
            {"id":"33333333-3333-3333-3333-333333333333","event_type":"{{eventType}}","event_status":"{{status}}","input_time":{{inputTimeJson}},"is_valid":true,"task_name":"基础信息","form_set_id":"11111111-1111-1111-1111-111111111111","form_set_name":"测试表单","event_type_definition_id":"22222222-2222-2222-2222-222222222222"}
            """;
    }

    private static FollowUpEdcScopeService CreateEdcScopeService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CubeDb"] = "Host=unused;Database=unused;Username=unused;Password=unused"
            })
            .Build();
        return new FollowUpEdcScopeService(configuration);
    }

    private static FollowUpVerifiedPackage CreateVerifiedPackage(
        string stagingPath,
        string exportPath,
        string fileHash) =>
        new(
            "package.fupkg",
            new string('0', 64),
            stagingPath,
            new FollowUpEncryptedEnvelope(),
            new FollowUpPackageManifest(),
            [
                new FollowUpTableManifestItem
                {
                    Schema = "public",
                    TableName = "patient",
                    Enabled = true,
                    ExportPath = exportPath,
                    FileHash = fileHash,
                    RecordCount = 1
                }
            ],
            new FollowUpSchemaSnapshot(),
            new FollowUpSchemaDiff());

}
