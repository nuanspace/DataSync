using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using DataSync.LHYY.V2.Services.FollowUp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
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
    [InlineData("随访", "已随访", null)]
    [InlineData("预问诊", "门诊结束", "2026-07-22T08:00:00")]
    [InlineData("门诊签到", "办理住院", "2026-07-22T08:00:00")]
    [InlineData("预问诊", "入组随访", "2026-07-22T08:00:00")]
    [InlineData("预问诊", "转诊", "2026-07-22T08:00:00")]
    [InlineData("转诊记录", "已确认", null)]
    public void 合格事件保留表单链接(string eventType, string status, string? inputTime)
    {
        var source = PatientEventJson(eventType, status, inputTime);

        var adapted = FollowUpTargetAdaptationService.AdaptRow("care", "patient_event", source);

        using var document = JsonDocument.Parse(adapted);
        Assert.Equal("11111111-1111-1111-1111-111111111111", document.RootElement.GetProperty("form_set_id").GetString());
        Assert.Equal("测试表单", document.RootElement.GetProperty("form_set_name").GetString());
        Assert.Equal("22222222-2222-2222-2222-222222222222", document.RootElement.GetProperty("event_type_definition_id").GetString());
    }

    [Theory]
    [InlineData("随访", "待审核", "2026-07-22T08:00:00")]
    [InlineData("预问诊", "门诊结束", null)]
    [InlineData("预问诊", "门诊签到", "2026-07-22T08:00:00")]
    [InlineData("转诊记录", "待确认", "2026-07-22T08:00:00")]
    public void 不合格事件保留基础信息但清空表单链接(string eventType, string status, string? inputTime)
    {
        var source = PatientEventJson(eventType, status, inputTime);

        var adapted = FollowUpTargetAdaptationService.AdaptRow("care", "patient_event", source);

        using var document = JsonDocument.Parse(adapted);
        Assert.Equal(eventType, document.RootElement.GetProperty("event_type").GetString());
        Assert.Equal(status, document.RootElement.GetProperty("event_status").GetString());
        Assert.Equal("基础信息", document.RootElement.GetProperty("task_name").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("form_set_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("form_set_name").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("event_type_definition_id").ValueKind);
    }

    [Fact]
    public void 已作废事件即使状态已审核也清空表单链接()
    {
        var source = PatientEventJson("随访", "已审核", null)
            .Replace("\"is_valid\":true", "\"is_valid\":false");

        var adapted = FollowUpTargetAdaptationService.AdaptRow("care", "patient_event", source);

        using var document = JsonDocument.Parse(adapted);
        Assert.False(document.RootElement.GetProperty("is_valid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("form_set_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("form_set_name").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("event_type_definition_id").ValueKind);
    }

    [Theory]
    [InlineData("\"is_valid\":true,", "")]
    [InlineData("\"is_valid\":true", "\"is_valid\":null")]
    public void 有效标记缺失或为空时即使已审核也清空表单链接(string oldValue, string newValue)
    {
        var source = PatientEventJson("随访", "已审核", null).Replace(oldValue, newValue);

        var adapted = FollowUpTargetAdaptationService.AdaptRow("care", "patient_event", source);

        using var document = JsonDocument.Parse(adapted);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("form_set_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("form_set_name").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("event_type_definition_id").ValueKind);
    }

    [Fact]
    public void 表单集为空时即使已审核也清空其余表单链接()
    {
        var source = PatientEventJson("随访", "已审核", null)
            .Replace("\"form_set_id\":\"11111111-1111-1111-1111-111111111111\"", "\"form_set_id\":null");

        var adapted = FollowUpTargetAdaptationService.AdaptRow("care", "patient_event", source);

        using var document = JsonDocument.Parse(adapted);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("form_set_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("form_set_name").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("event_type_definition_id").ValueKind);
    }

    [Fact]
    public void 后续合格增量可以恢复事件表单链接()
    {
        var pending = FollowUpTargetAdaptationService.AdaptRow(
            "care", "patient_event", PatientEventJson("随访", "待审核", "2026-07-22T08:00:00"));
        var completed = FollowUpTargetAdaptationService.AdaptRow(
            "care", "patient_event", PatientEventJson("随访", "已审核", "2026-07-22T08:00:00"));

        using var pendingDocument = JsonDocument.Parse(pending);
        using var completedDocument = JsonDocument.Parse(completed);
        Assert.Equal(JsonValueKind.Null, pendingDocument.RootElement.GetProperty("form_set_id").ValueKind);
        Assert.Equal("11111111-1111-1111-1111-111111111111", completedDocument.RootElement.GetProperty("form_set_id").GetString());
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
    public void V2导入契约默认值正确且传输协议保持V1()
    {
        var options = new FollowUpPackageImportOptions();
        var request = FollowUpRelayRequest.Create("list", "token", new { });

        Assert.Equal("followup-hospital-sync.v2", options.SupportedContractVersion);
        Assert.Equal("1.1.0", options.ImporterVersion);
        Assert.Equal("1.0", request.ProtocolVersion);
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

}
