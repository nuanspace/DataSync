using DataSync.LHYY.V2.Services.FollowUp;
using DataSync.LHYY.V2.Tools;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class FollowUpPatientIdentityBootstrapToolTests
{
    [Fact]
    public void 旧版映射按PatientId读取且Cube事务保持只读()
    {
        var columns = new HashSet<string>(
            ["patient_id", "hospital_code", "original_source_type", "first_package_id", "last_package_id"],
            StringComparer.OrdinalIgnoreCase);

        var sql = FollowUpPatientIdentityBootstrapTool.BuildLegacyReadSql(columns);

        Assert.Contains("SELECT source_map.patient_id", sql);
        Assert.Contains("patient.unique_id", sql);
        Assert.Contains("INNER JOIN public.patient", sql);
        Assert.Contains("source_map.hospital_code = @hospitalCode", sql);
        Assert.DoesNotContain("INSERT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 扩展版映射优先读取双端患者和唯一患者Id()
    {
        var columns = new HashSet<string>(
            [
                "patient_id", "source_patient_id", "target_patient_id",
                "source_unique_patient_id", "target_unique_patient_id",
                "identity_match_basis", "hospital_code", "original_source_type",
                "first_package_id", "last_package_id"
            ],
            StringComparer.OrdinalIgnoreCase);

        var sql = FollowUpPatientIdentityBootstrapTool.BuildLegacyReadSql(columns);

        Assert.Contains("source_map.source_patient_id", sql);
        Assert.Contains("source_map.target_patient_id", sql);
        Assert.Contains("source_map.source_unique_patient_id", sql);
        Assert.Contains("source_map.target_unique_patient_id", sql);
        Assert.Contains("source_map.identity_match_basis", sql);
    }

    [Fact]
    public void 旧版映射存在一对多或多对一时拒绝迁移()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        LegacyFollowUpPatientIdentityMapping[] duplicatedSource =
        [
            Mapping(source, target),
            Mapping(source, Guid.NewGuid())
        ];
        LegacyFollowUpPatientIdentityMapping[] duplicatedTarget =
        [
            Mapping(source, target),
            Mapping(Guid.NewGuid(), target)
        ];

        Assert.Throws<InvalidOperationException>(() =>
            FollowUpPatientIdentityBootstrapTool.ValidateMappings(duplicatedSource));
        Assert.Throws<InvalidOperationException>(() =>
            FollowUpPatientIdentityBootstrapTool.ValidateMappings(duplicatedTarget));
    }

    [Fact]
    public void 迁移和运行时映射只写DataSyncDb()
    {
        var bootstrapSql = FollowUpPatientIdentityBootstrapTool.BuildUpsertSql();
        var importSql = FollowUpPackageImportRepository.BuildPatientIdentityMapUpsertSql();
        var restoreSql = FollowUpPackageImportRepository.BuildPatientIdentityMapRollbackSql();

        Assert.All([bootstrapSql, importSql, restoreSql], sql =>
            Assert.Contains("lhyy.followup_patient_identity_map", sql));
        Assert.All([bootstrapSql, importSql, restoreSql], sql =>
            Assert.DoesNotContain("datasync.followup_patient_source_map", sql));
        Assert.Contains("DELETE FROM lhyy.followup_patient_identity_map", restoreSql);
        Assert.Contains("first_package_id = @packageId", restoreSql);
        Assert.Contains("last_package_id = @packageId", restoreSql);
    }

    [Fact]
    public void 当日迁移只创建DataSync管理表()
    {
        var root = FindRepositoryRoot();
        var sql = File.ReadAllText(Path.Combine(
            root,
            "DataSync.LHYY.V2",
            "Scripts",
            "202608",
            "20260811.sql"));

        Assert.Contains("CREATE TABLE IF NOT EXISTS lhyy.followup_patient_identity_map", sql);
        Assert.Contains("lhyy.followup_package_import_state", sql);
        Assert.DoesNotContain("CREATE SCHEMA IF NOT EXISTS datasync", sql);
        Assert.DoesNotContain("ALTER TABLE public.", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE care.", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE form.", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static LegacyFollowUpPatientIdentityMapping Mapping(Guid source, Guid target) =>
        new(source, target, null, null, "Id", null, "package-1", "package-1");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DataSync.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
