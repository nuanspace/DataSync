using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

namespace DataSync.LHYY.V2.Services.FollowUp;

public sealed record FollowUpEdcScopePlan(
    IReadOnlyCollection<Guid> PatientIds,
    IReadOnlyCollection<Guid> EdcProjectIds,
    bool ShouldApply);

internal sealed class FollowUpPackageScope
{
    public HashSet<Guid> PatientIds { get; } = [];
    public HashSet<Guid> ProjectIds { get; } = [];
    public HashSet<Guid> EdcProjectIds { get; } = [];
}

public sealed class FollowUpEdcScopeService(IConfiguration configuration)
{
    private static readonly string[] RequiredColumns =
    [
        "id", "created_time", "patient_id", "hospital_id", "department_id", "ward_id", "project_id"
    ];

    private readonly string _cubeConnectionString = configuration.GetConnectionString("CubeDb")
        ?? throw new InvalidOperationException("未找到连接字符串 'CubeDb'");

    public async Task<FollowUpEdcScopePlan> PrepareAsync(
        FollowUpVerifiedPackage package,
        FollowUpSchemaDecision? schemaDecision,
        CancellationToken cancellationToken)
    {
        var packageScope = await ReadPackageScopeAsync(package, schemaDecision, cancellationToken);
        if (packageScope.PatientIds.Count == 0
            && packageScope.ProjectIds.Count == 0
            && packageScope.EdcProjectIds.Count == 0)
            return CreatePlan(packageScope, false);

        await using var connection = new NpgsqlConnection(_cubeConnectionString);
        await connection.OpenAsync(cancellationToken);
        var hasTargetEdcData = packageScope.EdcProjectIds.Overlaps(packageScope.ProjectIds)
            || await HasTargetEdcDataAsync(
                connection,
                packageScope.PatientIds,
                packageScope.ProjectIds,
                cancellationToken);
        var plan = CreatePlan(packageScope, hasTargetEdcData);

        if (plan.ShouldApply)
        {
            var columns = await GetScopeMapColumnsAsync(connection, cancellationToken);
            var missing = GetMissingRequiredColumns(columns);
            if (missing.Count > 0)
            {
                throw new FollowUpPackageException(
                    FollowUpErrorCodes.SchemaReviewRequired,
                    $"EDC 患者可见性依赖 public.patient_data_scope_map，目标库缺少字段：{string.Join(", ", missing)}。");
            }
        }

        return plan;
    }

    public async Task<int> ApplyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FollowUpEdcScopePlan plan,
        CancellationToken cancellationToken)
    {
        if (!plan.ShouldApply || (plan.PatientIds.Count == 0 && plan.EdcProjectIds.Count == 0))
            return 0;

        await using var command = new NpgsqlCommand(BuildUpsertSql(), connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("patient_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = plan.PatientIds.ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("edc_project_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = plan.EdcProjectIds.ToArray()
        });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static string BuildUpsertSql() => """
        WITH candidate AS (
            SELECT p.id AS patient_id, pe.project_id
            FROM public.patient p
            INNER JOIN datasync.followup_patient_source_map source_map ON source_map.patient_id = p.id
            INNER JOIN care.patient_event pe ON pe.patient_id = p.id
            WHERE p.id = ANY(@patient_ids) OR pe.project_id = ANY(@edc_project_ids)

            UNION

            SELECT p.id AS patient_id, p.project_id
            FROM public.patient p
            INNER JOIN datasync.followup_patient_source_map source_map ON source_map.patient_id = p.id
            WHERE p.project_id IS NOT NULL
              AND (p.id = ANY(@patient_ids) OR p.project_id = ANY(@edc_project_ids))
        )
        , desired AS (
            SELECT DISTINCT
                md5(candidate.patient_id::text || ':' || candidate.project_id::text)::uuid AS id,
                candidate.patient_id,
                fp.hospital_id,
                fp.department_id,
                fp.ward_id,
                candidate.project_id
            FROM candidate
            INNER JOIN form.form_project fp ON fp.id = candidate.project_id
            WHERE EXISTS (
              SELECT 1
              FROM form.form_form_set fs
              WHERE fs.project_id = candidate.project_id
                AND fs.type = 'edc')
        ), updated AS (
            UPDATE public.patient_data_scope_map scope_map
            SET hospital_id = desired.hospital_id,
                department_id = desired.department_id,
                ward_id = desired.ward_id
            FROM desired
            WHERE scope_map.patient_id = desired.patient_id
              AND scope_map.project_id = desired.project_id
            RETURNING scope_map.patient_id, scope_map.project_id
        )
        INSERT INTO public.patient_data_scope_map
            (id, created_time, patient_id, hospital_id, department_id, ward_id, project_id)
        SELECT desired.id, CURRENT_TIMESTAMP, desired.patient_id,
               desired.hospital_id, desired.department_id, desired.ward_id, desired.project_id
        FROM desired
        WHERE NOT EXISTS (
            SELECT 1
            FROM updated
            WHERE updated.patient_id = desired.patient_id
              AND updated.project_id = desired.project_id)
        """;

    internal static List<string> GetMissingRequiredColumns(IEnumerable<string> columns)
    {
        var actual = columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return RequiredColumns.Where(column => !actual.Contains(column)).ToList();
    }

    internal static FollowUpPackageScope AnalyzeRows(
        string schema,
        string table,
        IEnumerable<string> rows)
    {
        var scope = new FollowUpPackageScope();
        var kind = ResolveTableKind(schema, table);
        foreach (var row in rows)
            AnalyzeRow(scope, kind, row);
        return scope;
    }

    internal static FollowUpEdcScopePlan CreatePlan(
        FollowUpPackageScope packageScope,
        bool hasTargetEdcData)
    {
        var edcProjectIds = packageScope.EdcProjectIds.ToHashSet();
        if (hasTargetEdcData)
            edcProjectIds.UnionWith(packageScope.ProjectIds);
        return new(
            packageScope.PatientIds.ToArray(),
            edcProjectIds.ToArray(),
            hasTargetEdcData || edcProjectIds.Count > 0);
    }

    private static async Task<FollowUpPackageScope> ReadPackageScopeAsync(
        FollowUpVerifiedPackage package,
        FollowUpSchemaDecision? schemaDecision,
        CancellationToken cancellationToken)
    {
        var scope = new FollowUpPackageScope();
        foreach (var table in package.TableManifest.Where(item =>
                     item.Enabled && !item.Skipped && !string.IsNullOrWhiteSpace(item.ExportPath)))
        {
            var target = FollowUpSchemaDecisionProcessor.MapManifest(table, schemaDecision);
            var kind = ResolveTableKind(target.Schema, target.TableName);
            if (kind == PackageTableKind.None)
                continue;

            var filePath = SafeStagingPath(package.StagingPath, table.ExportPath!);
            if (string.IsNullOrWhiteSpace(table.FileHash))
                throw new InvalidDataException($"表 {table.Schema}.{table.TableName} 缺少导入文件 hash。");
            await using var snapshot = await FollowUpPackageImportService.OpenVerifiedImportSnapshotAsync(
                filePath,
                table.FileHash,
                cancellationToken);
            using var reader = new StreamReader(snapshot, leaveOpen: true);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var mappedLine = FollowUpSchemaDecisionProcessor.MapRow(
                    line, table.Schema, table.TableName, schemaDecision);
                AnalyzeRow(scope, kind, mappedLine);
            }
        }

        return scope;
    }

    private static void AnalyzeRow(FollowUpPackageScope scope, PackageTableKind kind, string line)
    {
        using var document = JsonDocument.Parse(line);
        var row = document.RootElement;
        switch (kind)
        {
            case PackageTableKind.Patient:
                if (ReadGuid(row, "id") is { } patientId)
                    scope.PatientIds.Add(patientId);
                if (ReadGuid(row, "project_id") is { } patientProjectId)
                    scope.ProjectIds.Add(patientProjectId);
                break;
            case PackageTableKind.PatientEvent:
                if (ReadGuid(row, "patient_id") is { } eventPatientId)
                    scope.PatientIds.Add(eventPatientId);
                if (ReadGuid(row, "project_id") is { } eventProjectId)
                    scope.ProjectIds.Add(eventProjectId);
                break;
            case PackageTableKind.FormSet:
                if (ReadString(row, "type") == "edc"
                    && ReadGuid(row, "project_id") is { } edcProjectId)
                    scope.EdcProjectIds.Add(edcProjectId);
                break;
            case PackageTableKind.FormProject:
                if (ReadGuid(row, "id") is { } formProjectId)
                    scope.ProjectIds.Add(formProjectId);
                break;
        }
    }

    private static async Task<bool> HasTargetEdcDataAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<Guid> patientIds,
        IReadOnlyCollection<Guid> projectIds,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT
                EXISTS (
                    SELECT 1
                    FROM care.patient_event pe
                    INNER JOIN form.form_form_set fs ON fs.project_id = pe.project_id
                    WHERE pe.patient_id = ANY(@patient_ids)
                      AND fs.type = 'edc')
                OR EXISTS (
                    SELECT 1
                    FROM public.patient p
                    INNER JOIN form.form_form_set fs ON fs.project_id = p.project_id
                    WHERE p.id = ANY(@patient_ids)
                      AND fs.type = 'edc')
                OR EXISTS (
                    SELECT 1
                    FROM form.form_form_set fs
                    WHERE fs.project_id = ANY(@project_ids)
                      AND fs.type = 'edc')
            """, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("patient_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = patientIds.ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("project_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = projectIds.ToArray()
        });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<List<string>> GetScopeMapColumnsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'patient_data_scope_map'
            """, connection);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(0));
        return columns;
    }

    private static PackageTableKind ResolveTableKind(string schema, string table) =>
        (schema.ToLowerInvariant(), table.ToLowerInvariant()) switch
        {
            ("public", "patient") => PackageTableKind.Patient,
            ("care", "patient_event") => PackageTableKind.PatientEvent,
            ("form", "form_form_set") => PackageTableKind.FormSet,
            ("form", "form_project") => PackageTableKind.FormProject,
            _ => PackageTableKind.None
        };

    private static Guid? ReadGuid(JsonElement row, string propertyName) =>
        row.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && Guid.TryParse(value.GetString(), out var result)
            ? result
            : null;

    private static string ReadString(JsonElement row, string propertyName) =>
        row.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string SafeStagingPath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("数据文件路径逃逸 staging 目录。");
        return target;
    }

    private enum PackageTableKind
    {
        None,
        Patient,
        PatientEvent,
        FormSet,
        FormProject
    }
}
