using DataSync.LHYY.V2.Tools;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class ExternalCubeCompatibilityToolTests
{
    [Fact]
    public void 完整结构通过兼容性检查()
    {
        var columns = CreateCompatibleColumns();

        var issues = ExternalCubeCompatibilityTool.Evaluate(
            columns,
            CreateCompatiblePrivileges(),
            CreateCompatibleSchemas(),
            vectorInstalled: true);

        Assert.Empty(issues);
    }

    [Fact]
    public void 缺少患者判定字段和Vector时返回明确问题()
    {
        var columns = CreateCompatibleColumns();
        ((HashSet<string>)columns["public.unique_patient"]).Remove("gender");

        var issues = ExternalCubeCompatibilityTool.Evaluate(
            columns,
            CreateCompatiblePrivileges(),
            CreateCompatibleSchemas(),
            vectorInstalled: false);

        Assert.Contains("缺少字段 public.unique_patient.gender", issues);
        Assert.Contains("缺少 form schema 下的 vector 扩展", issues);
    }

    [Fact]
    public void 患者身份合并只要求Cube既有自然人读取字段()
    {
        var uniquePatient = ExternalCubeCompatibilityTool.DefaultRequirements
            .Single(item => item.Table == "public.unique_patient");

        Assert.Contains("SELECT", uniquePatient.Privileges);
        Assert.Contains("sid_number", uniquePatient.Columns);
        Assert.Contains("name", uniquePatient.Columns);
        Assert.Contains("birthday", uniquePatient.Columns);
        Assert.Contains("gender", uniquePatient.Columns);
        Assert.DoesNotContain(
            ExternalCubeCompatibilityTool.DefaultRequirements,
            item => item.Table.StartsWith("datasync.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 缺少目标表时拒绝通过()
    {
        var columns = CreateCompatibleColumns();
        columns.Remove("public.patient");

        var issues = ExternalCubeCompatibilityTool.Evaluate(
            columns,
            CreateCompatiblePrivileges(),
            CreateCompatibleSchemas(),
            vectorInstalled: true);

        Assert.Contains("缺少表 public.patient", issues);
    }

    [Fact]
    public void 缺少Edc依赖表字段和权限时拒绝通过()
    {
        var columns = CreateCompatibleColumns();
        columns.Remove("form.form_form_set");
        ((HashSet<string>)columns["form.form_project"]).Remove("ward_id");
        var privileges = CreateCompatiblePrivileges();
        ((HashSet<string>)privileges["public.patient_data_scope_map"]).Remove("UPDATE");
        var schemas = CreateCompatibleSchemas();
        schemas.Remove("target");

        var issues = ExternalCubeCompatibilityTool.Evaluate(columns, privileges, schemas, vectorInstalled: true);

        Assert.Contains("缺少表 form.form_form_set", issues);
        Assert.Contains("缺少字段 form.form_project.ward_id", issues);
        Assert.Contains("当前账号缺少权限 public.patient_data_scope_map:UPDATE", issues);
        Assert.Contains("当前账号缺少 schema 使用权限 target:USAGE", issues);
    }

    [Fact]
    public void 默认契约覆盖云端V2全部二十三张目标表()
    {
        var expected = new[]
        {
            "system.sys_hospital", "system.sys_department", "form.form_project",
            "care.event_type_definition", "care.event_type_config", "form.form_form_set",
            "form.form_form", "form.form_card", "form.form_question", "form.form_linker_rule",
            "public.unique_patient", "public.patient", "care.patient_event",
            "care.patient_event_form_audit_state", "care.patient_hospitalized", "care.patient_outpatient",
            "followup.followup_file_list", "public.table_definition", "public.dict_concept",
            "public.column_definition", "report.child_multi_quick_suite",
            "report.child_multi_quick_suite_item", "form.form_progress_excluded_question"
        };

        var actual = ExternalCubeCompatibilityTool.DefaultRequirements
            .Select(item => item.Table)
            .Where(table => table is not "public.patient_data_scope_map")
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void 默认必要字段与当前Bio模型一致()
    {
        var modelTypes = typeof(Bio.Models.patient).Assembly.GetTypes()
            .GroupBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var modelBackedRequirements = ExternalCubeCompatibilityTool.DefaultRequirements
            .Where(item => item.Table is not "public.patient_data_scope_map"
                && !item.Table.StartsWith("report.", StringComparison.OrdinalIgnoreCase));

        foreach (var requirement in modelBackedRequirements)
        {
            var typeName = requirement.Table[(requirement.Table.IndexOf('.') + 1)..];
            Assert.True(modelTypes.TryGetValue(typeName, out var modelType), $"未找到模型 {requirement.Table}");
            var properties = modelType!.GetProperties()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(requirement.Columns, column =>
                Assert.True(properties.Contains(column), $"模型 {requirement.Table} 缺少契约字段 {column}"));
        }
    }

    [Fact]
    public void 动态表要求Target权限和PatientEventId字段()
    {
        var requirement = new ExternalCubeTableRequirement(
            "target.form_answer_1",
            ["patient_event_id"],
            ["INSERT", "UPDATE"]);
        var columns = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [requirement.Table] = new HashSet<string>(["id"], StringComparer.OrdinalIgnoreCase)
        };
        var privileges = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [requirement.Table] = new HashSet<string>(["INSERT"], StringComparer.OrdinalIgnoreCase)
        };

        var issues = ExternalCubeCompatibilityTool.Evaluate(
            columns,
            privileges,
            new HashSet<string>(["target"], StringComparer.OrdinalIgnoreCase),
            vectorInstalled: true,
            [requirement],
            checkVector: false);

        Assert.Contains("缺少字段 target.form_answer_1.patient_event_id", issues);
        Assert.Contains("当前账号缺少权限 target.form_answer_1:UPDATE", issues);
    }

    [Fact]
    public void 首包访问检查按导入策略要求最小权限()
    {
        ExternalCubePackageTable[] tables =
        [
            new("system", "sys_hospital", "UseExistingById"),
            new("public", "unique_patient", "InsertIfMissing"),
            new("target", "form_answer_1", "Upsert")
        ];
        var privileges = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["system.sys_hospital"] = new HashSet<string>(["SELECT"], StringComparer.OrdinalIgnoreCase),
            ["public.unique_patient"] = new HashSet<string>(["INSERT"], StringComparer.OrdinalIgnoreCase),
            ["target.form_answer_1"] = new HashSet<string>(["INSERT", "UPDATE", "SELECT"], StringComparer.OrdinalIgnoreCase)
        };
        var schemas = new HashSet<string>(["system", "public", "target"], StringComparer.OrdinalIgnoreCase);

        var compatible = ExternalCubeCompatibilityTool.EvaluatePackageAccess(tables, privileges, schemas);
        ((HashSet<string>)privileges["target.form_answer_1"]).Remove("UPDATE");
        var rejected = ExternalCubeCompatibilityTool.EvaluatePackageAccess(tables, privileges, schemas);

        Assert.Empty(compatible);
        Assert.Contains("当前账号缺少权限 target.form_answer_1:UPDATE", rejected);
        Assert.DoesNotContain(rejected, issue => issue.Contains("unique_patient:SELECT", StringComparison.Ordinal));
    }

    [Fact]
    public void 启动兼容检查提前核对完整备份所需读取权限()
    {
        var source = ReadToolSource();

        Assert.Contains("ReadBackupPrivilegeIssuesAsync", source);
        Assert.Contains("has_table_privilege(current_user, relation.oid, 'SELECT')", source);
        Assert.Contains("has_sequence_privilege(current_user, sequence_relation.oid, 'SELECT')", source);
        Assert.Contains("has_schema_privilege(current_user, namespace.oid, 'USAGE')", source);
    }

    private static string ReadToolSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DataSync.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(
            directory!.FullName,
            "DataSync.LHYY.V2",
            "Tools",
            "ExternalCubeCompatibilityTool.cs"));
    }

    private static Dictionary<string, IReadOnlySet<string>> CreateCompatibleColumns() =>
        ExternalCubeCompatibilityTool.DefaultRequirements.ToDictionary(
            requirement => requirement.Table,
            requirement => (IReadOnlySet<string>)new HashSet<string>(requirement.Columns, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, IReadOnlySet<string>> CreateCompatiblePrivileges() =>
        ExternalCubeCompatibilityTool.DefaultRequirements.ToDictionary(
            requirement => requirement.Table,
            requirement => (IReadOnlySet<string>)new HashSet<string>(requirement.Privileges, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> CreateCompatibleSchemas() =>
        new(["system", "care", "form", "public", "followup", "report", "target"], StringComparer.OrdinalIgnoreCase);
}
