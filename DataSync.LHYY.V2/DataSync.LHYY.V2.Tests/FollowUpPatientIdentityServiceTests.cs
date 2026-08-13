using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Services.FollowUp;
using System.Text.Json;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class FollowUpPatientIdentityServiceTests
{
    private static readonly Guid SourceUniquePatientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TargetUniquePatientId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SourcePatientId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid TargetPatientId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid HospitalId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ProjectId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void 原始UniquePatientId优先于自然人候选()
    {
        var mapping = FollowUpPatientIdentityService.SelectUniquePatientMapping(
            SourceUniquePatientId,
            [
                new(SourceUniquePatientId, TargetUniquePatientId, FollowUpPatientIdentityMatchBasis.SidNumber),
                new(SourceUniquePatientId, SourceUniquePatientId, FollowUpPatientIdentityMatchBasis.Id)
            ]);

        Assert.Equal(SourceUniquePatientId, mapping.TargetUniquePatientId);
        Assert.Equal(FollowUpPatientIdentityMatchBasis.Id, mapping.MatchBasis);
        Assert.True(mapping.TargetExisted);
    }

    [Fact]
    public void 自然人多重命中阻断且错误不包含身份明文()
    {
        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPatientIdentityService.SelectUniquePatientMapping(
                SourceUniquePatientId,
                [
                    new(SourceUniquePatientId, TargetUniquePatientId, FollowUpPatientIdentityMatchBasis.SidNumber),
                    new(SourceUniquePatientId, Guid.Parse("55555555-5555-5555-5555-555555555555"), FollowUpPatientIdentityMatchBasis.SidNumber)
                ]));

        Assert.Equal(FollowUpErrorCodes.PatientIdentityConflict, exception.ErrorCode);
        Assert.Contains("身份证", exception.Message);
        Assert.Contains("2 条", exception.Message);
        Assert.DoesNotContain("111111", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 混合规则多重命中会报告全部判定规则和数量()
    {
        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPatientIdentityService.SelectUniquePatientMapping(
                SourceUniquePatientId,
                [
                    new(SourceUniquePatientId, TargetUniquePatientId, FollowUpPatientIdentityMatchBasis.SidNumber),
                    new(SourceUniquePatientId, Guid.Parse("55555555-5555-5555-5555-555555555555"), FollowUpPatientIdentityMatchBasis.Demographics)
                ]));

        Assert.Contains("身份证", exception.Message);
        Assert.Contains("姓名+出生日期+性别", exception.Message);
        Assert.Contains("2 条", exception.Message);
    }

    [Fact]
    public void 身份证和三要素SQL遵循保守匹配规则()
    {
        var sql = FollowUpPatientIdentityService.UniquePatientCandidateSql;

        Assert.Contains("UPPER(BTRIM(input.\"sidNumber\"))", sql, StringComparison.Ordinal);
        Assert.Contains("target.id = source.source_id", sql, StringComparison.Ordinal);
        Assert.Contains("source.sid_number IS NULL OR target.normalized_sid_number IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("source.name IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("source.birthday IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("source.gender IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("target.birthday = source.birthday", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Patient候选SQL限定唯一患者医院和课题且不筛IsValid()
    {
        var sql = FollowUpPatientIdentityService.PatientCandidateSql;

        Assert.Contains("patient.unique_id = source.target_unique_patient_id", sql, StringComparison.Ordinal);
        Assert.Contains("patient.hospital_id = source.hospital_id", sql, StringComparison.Ordinal);
        Assert.Contains("patient.project_id = source.project_id", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("is_valid", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 持久来源映射查询按医院编码隔离()
    {
        var sql = FollowUpPatientIdentityService.PersistedPatientAliasSql;

        Assert.Contains("hospital_code = @hospitalCode", sql, StringComparison.Ordinal);
        Assert.Contains("source_patient_id = ANY(@patientIds)", sql, StringComparison.Ordinal);
        Assert.Contains("source_unique_patient_id = ANY(@uniquePatientIds)", sql, StringComparison.Ordinal);
        Assert.Contains("hospital_code = @hospitalCode", FollowUpPatientIdentityService.TargetPatientAliasSql);
    }

    [Fact]
    public void 唯一范围Patient复用院端Id并保护院端字段()
    {
        var source = CreatePatientInput();

        var mapping = FollowUpPatientIdentityService.SelectPatientMapping(
            source,
            TargetUniquePatientId,
            FollowUpPatientIdentityMatchBasis.SidNumber,
            [new(source.SourcePatientId, TargetPatientId, TargetUniquePatientId, HospitalId, ProjectId, false, false, true)]);

        Assert.Equal(TargetPatientId, mapping.TargetPatientId);
        Assert.True(mapping.PreserveTargetPatient);
    }

    [Fact]
    public void 同一唯一患者医院课题存在多条Patient时阻断()
    {
        var source = CreatePatientInput();

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPatientIdentityService.SelectPatientMapping(
                source,
                TargetUniquePatientId,
                FollowUpPatientIdentityMatchBasis.SidNumber,
                [
                    new(source.SourcePatientId, TargetPatientId, TargetUniquePatientId, HospitalId, ProjectId, false, false, true),
                    new(source.SourcePatientId, Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), TargetUniquePatientId, HospitalId, ProjectId, false, false, true)
                ]));

        Assert.Equal(FollowUpErrorCodes.PatientIdentityConflict, exception.ErrorCode);
        Assert.Contains("unique_id+hospital_id+project_id", exception.Message);
        Assert.Contains("2 条", exception.Message);
    }

    [Fact]
    public void 相同PatientId被其他医院或课题占用时阻断()
    {
        var source = CreatePatientInput();

        var exception = Assert.Throws<FollowUpPackageException>(() =>
            FollowUpPatientIdentityService.SelectPatientMapping(
                source,
                TargetUniquePatientId,
                FollowUpPatientIdentityMatchBasis.Id,
                [new(source.SourcePatientId, SourcePatientId, TargetUniquePatientId, Guid.NewGuid(), ProjectId, true, false, false)]));

        Assert.Equal(FollowUpErrorCodes.PatientIdentityConflict, exception.ErrorCode);
        Assert.Contains("医院或课题", exception.Message);
    }

    [Fact]
    public void 自然匹配后重写患者及所有下游引用并保护院端Patient字段()
    {
        var plan = CreatePlan(preserveTargetPatient: true);

        var patient = plan.AdaptRow(
            "public",
            "patient",
            $$"""{"id":"{{SourcePatientId}}","unique_id":"{{SourceUniquePatientId}}","name":"云端姓名","source_type":"followup"}""");
        var patientEvent = plan.AdaptRow(
            "care",
            "patient_event",
            $$"""{"id":"55555555-5555-5555-5555-555555555555","patient_id":"{{SourcePatientId}}","unique_patient_id":"{{SourceUniquePatientId}}"}""");
        var hospitalized = plan.AdaptRow(
            "care",
            "patient_hospitalized",
            $$"""{"id":"66666666-6666-6666-6666-666666666666","patient_id":"{{SourcePatientId}}"}""");
        var dynamicRow = plan.AdaptRow(
            "target",
            "answer_table",
            $$"""{"id":"77777777-7777-7777-7777-777777777777","patient_id":"{{SourcePatientId}}"}""");

        Assert.True(patient.SkipWrite);
        Assert.Equal(SourcePatientId, patient.Patient!.SourcePatientId);
        Assert.Equal(TargetPatientId, patient.Patient.TargetPatientId);
        AssertJsonGuid(patient.Row, "id", TargetPatientId);
        AssertJsonGuid(patient.Row, "unique_id", TargetUniquePatientId);
        AssertJsonGuid(patientEvent.Row, "patient_id", TargetPatientId);
        AssertJsonGuid(patientEvent.Row, "unique_patient_id", TargetUniquePatientId);
        AssertJsonGuid(hospitalized.Row, "patient_id", TargetPatientId);
        AssertJsonGuid(dynamicRow.Row, "patient_id", TargetPatientId);
    }

    [Fact]
    public void 原PatientId相同时继续执行现有Upsert()
    {
        var plan = CreatePlan(preserveTargetPatient: false, targetPatientId: SourcePatientId);

        var patient = plan.AdaptRow(
            "public",
            "patient",
            $$"""{"id":"{{SourcePatientId}}","unique_id":"{{SourceUniquePatientId}}","name":"云端姓名"}""");

        Assert.False(patient.SkipWrite);
        AssertJsonGuid(patient.Row, "id", SourcePatientId);
        AssertJsonGuid(patient.Row, "unique_id", TargetUniquePatientId);
    }

    [Fact]
    public void 复用院端UniquePatient时保留院端现有字段()
    {
        var plan = CreatePlan(preserveTargetPatient: true);

        var uniquePatient = plan.AdaptRow(
            "public",
            "unique_patient",
            $$"""{"id":"{{SourceUniquePatientId}}","name":"云端姓名"}""");

        Assert.True(uniquePatient.SkipWrite);
        AssertJsonGuid(uniquePatient.Row, "id", TargetUniquePatientId);
    }

    [Fact]
    public void 增量Edc计划使用院端PatientId()
    {
        var plan = CreatePlan(preserveTargetPatient: true);
        var remapped = plan.Remap(new FollowUpEdcScopePlan(
            [SourcePatientId],
            [Guid.Parse("88888888-8888-8888-8888-888888888888")],
            [],
            true));

        Assert.Equal([TargetPatientId], remapped.PatientIds);
    }

    [Fact]
    public void 未解析的下游PatientId会失败关闭()
    {
        var plan = new FollowUpPatientIdentityPlan(
            new Dictionary<Guid, FollowUpUniquePatientIdentityMap>(),
            new Dictionary<Guid, FollowUpPatientIdentityMap>());

        var exception = Assert.Throws<FollowUpPackageException>(() => plan.AdaptRow(
            "care",
            "patient_event",
            $$"""{"patient_id":"{{SourcePatientId}}"}"""));

        Assert.Equal(FollowUpErrorCodes.PatientIdentityConflict, exception.ErrorCode);
        Assert.Contains("patient_id", exception.Message);
    }

    private static FollowUpPatientIdentityPlan CreatePlan(bool preserveTargetPatient, Guid? targetPatientId = null) =>
        new(
            new Dictionary<Guid, FollowUpUniquePatientIdentityMap>
            {
                [SourceUniquePatientId] = new(
                    SourceUniquePatientId,
                    TargetUniquePatientId,
                    FollowUpPatientIdentityMatchBasis.SidNumber,
                    true)
            },
            new Dictionary<Guid, FollowUpPatientIdentityMap>
            {
                [SourcePatientId] = new(
                    SourcePatientId,
                    targetPatientId ?? TargetPatientId,
                    SourceUniquePatientId,
                    TargetUniquePatientId,
                    FollowUpPatientIdentityMatchBasis.SidNumber,
                    preserveTargetPatient,
                    "followup")
            });

    private static FollowUpPatientIdentityInput CreatePatientInput() =>
        new(
            SourcePatientId,
            SourceUniquePatientId,
            HospitalId,
            ProjectId,
            "followup");

    private static void AssertJsonGuid(string json, string propertyName, Guid expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, document.RootElement.GetProperty(propertyName).GetGuid());
    }
}
