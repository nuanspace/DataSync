using Microsoft.Extensions.Configuration;
using DataSync.LHYY.V2.Services.FollowUp;
using Npgsql;
using NpgsqlTypes;
using System.Text;

namespace DataSync.LHYY.V2.Tools;

public static class ExternalCubeCompatibilityTool
{
    private const string CommandName = "cube-compat-check";

    public static IReadOnlyList<ExternalCubeTableRequirement> DefaultRequirements { get; } =
    [
        // 与云端 HospitalDataTableSelectionService.GetDefaultTables 的 v3 默认清单保持一致。
        Required("system.sys_hospital", ["id"], ["SELECT"]),
        Required("system.sys_department", ["id", "hospital_id"], ["INSERT"]),
        Required("form.form_project", ["id", "hospital_id", "department_id", "ward_id"], ["SELECT", "INSERT", "UPDATE"]),
        Required("care.event_type_definition", ["id", "project_id"], ["INSERT", "UPDATE"]),
        Required("care.event_type_config", ["id", "project_id"], ["INSERT", "UPDATE"]),
        Required("form.form_form_set", ["id", "project_id", "type"], ["SELECT", "INSERT", "UPDATE"]),
        Required("form.form_form", ["id", "project_id"], ["INSERT", "UPDATE"]),
        Required("form.form_card", ["id", "project_id"], ["INSERT", "UPDATE"]),
        Required("form.form_question", ["id", "project_id", "table_name"], ["SELECT", "INSERT", "UPDATE"]),
        Required("form.form_linker_rule", ["id", "formset_id"], ["INSERT", "UPDATE"]),
        Required("public.unique_patient", ["id", "sid_number", "name", "birthday", "gender"], ["SELECT", "INSERT"]),
        Required("public.patient", ["id", "hospital_id", "project_id", "unique_id", "source_type"], ["SELECT", "INSERT", "UPDATE"]),
        Required("care.patient_event",
            ["id", "patient_id", "project_id", "form_set_id", "form_set_name", "event_type_definition_id"],
            ["SELECT", "INSERT", "UPDATE"]),
        Required("care.patient_event_form_audit_state", ["patient_event_id", "form_id"], ["INSERT", "UPDATE"]),
        Required("care.patient_hospitalized", ["id", "patient_id", "patient_event_id"], ["INSERT", "UPDATE"]),
        Required("care.patient_outpatient",
            ["id", "patient_id", "patient_event_id", "diagnosis_file", "physical_exam_file", "remark_file"],
            ["INSERT", "UPDATE"]),
        Required("followup.followup_file_list", ["id"], ["INSERT", "UPDATE"]),
        Required("public.table_definition", ["id"], ["INSERT"]),
        Required("public.dict_concept", ["concept_id"], ["INSERT"]),
        Required("public.column_definition", ["id"], ["INSERT"]),
        Required("report.child_multi_quick_suite", ["id", "project_id"], ["INSERT", "UPDATE"]),
        Required("report.child_multi_quick_suite_item", ["id", "suite_id"], ["INSERT", "UPDATE"]),
        Required("form.form_progress_excluded_question", ["id", "project_id"], ["INSERT", "UPDATE"]),

        // LHYY 只使用 CubeDb 已有业务结构；患者身份映射保存在 DataSyncDb。
        Required("public.patient_data_scope_map",
            ["id", "created_time", "patient_id", "hospital_id", "department_id", "ward_id", "project_id"],
            ["SELECT", "INSERT", "UPDATE"])
    ];

    private static readonly string[] RequiredSchemas =
        ["system", "care", "form", "public", "followup", "report", "target"];

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Environment.CurrentDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables()
                .Build();
            var connectionString = configuration.GetConnectionString("CubeDb")
                ?? throw new InvalidOperationException("未找到连接字符串 'CubeDb'。");

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                await readOnly.ExecuteNonQueryAsync();

            var columns = await ReadColumnsAsync(connection, transaction, RequiredSchemas);
            var privileges = await ReadPrivilegesAsync(connection, transaction, DefaultRequirements);
            var schemasWithUsage = await ReadSchemasWithUsageAsync(connection, transaction, RequiredSchemas);
            var vectorInstalled = await HasVectorExtensionAsync(connection, transaction);
            var issues = Evaluate(columns, privileges, schemasWithUsage, vectorInstalled).ToList();
            issues.AddRange(await ReadBackupPrivilegeIssuesAsync(connection, transaction));

            if (issues.Count == 0)
            {
                var dynamicTables = await ReadReferencedDynamicTablesAsync(connection, transaction);
                issues.AddRange(dynamicTables.Issues);
                var dynamicRequirements = dynamicTables.TableNames
                    .Select(table => Required($"target.{table}", ["patient_event_id"], ["INSERT", "UPDATE"]))
                    .ToArray();
                if (dynamicRequirements.Length > 0)
                {
                    var combined = DefaultRequirements.Concat(dynamicRequirements).ToArray();
                    privileges = await ReadPrivilegesAsync(connection, transaction, combined);
                    issues.AddRange(Evaluate(
                        columns,
                        privileges,
                        schemasWithUsage,
                        vectorInstalled,
                        dynamicRequirements,
                        checkVector: false));
                }
            }

            await transaction.RollbackAsync();

            if (issues.Count > 0)
            {
                Console.Error.WriteLine("现有 CubeDb 未通过只读兼容性检查：");
                foreach (var issue in issues.Distinct(StringComparer.OrdinalIgnoreCase))
                    Console.Error.WriteLine($"- {issue}");
                return 1;
            }

            var target = new NpgsqlConnectionStringBuilder(connectionString);
            Console.WriteLine($"现有 CubeDb 只读兼容性检查通过：Host={target.Host}; Port={target.Port}; Database={target.Database}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"现有 CubeDb 只读兼容性检查失败：{ex.Message}");
            return 1;
        }
    }

    public static IReadOnlyList<string> Evaluate(
        IReadOnlyDictionary<string, IReadOnlySet<string>> columnsByTable,
        IReadOnlyDictionary<string, IReadOnlySet<string>> privilegesByTable,
        IReadOnlySet<string> schemasWithUsage,
        bool vectorInstalled,
        IReadOnlyCollection<ExternalCubeTableRequirement>? requirements = null,
        bool checkVector = true)
    {
        requirements ??= DefaultRequirements;
        var issues = new List<string>();
        foreach (var requirement in requirements)
        {
            if (!columnsByTable.TryGetValue(requirement.Table, out var actualColumns))
            {
                issues.Add($"缺少表 {requirement.Table}");
                continue;
            }

            foreach (var column in requirement.Columns)
                if (!actualColumns.Contains(column))
                    issues.Add($"缺少字段 {requirement.Table}.{column}");

            privilegesByTable.TryGetValue(requirement.Table, out var actualPrivileges);
            foreach (var privilege in requirement.Privileges)
                if (actualPrivileges is null || !actualPrivileges.Contains(privilege))
                    issues.Add($"当前账号缺少权限 {requirement.Table}:{privilege}");
        }

        foreach (var schema in requirements.Select(item => item.Schema)
                     .Concat(requirements == DefaultRequirements ? RequiredSchemas : [])
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            if (!schemasWithUsage.Contains(schema))
                issues.Add($"当前账号缺少 schema 使用权限 {schema}:USAGE");

        if (checkVector && !vectorInstalled)
            issues.Add("缺少 form schema 下的 vector 扩展");

        return issues;
    }

    public static IReadOnlyList<string> EvaluatePackageAccess(
        IReadOnlyCollection<ExternalCubePackageTable> tables,
        IReadOnlyDictionary<string, IReadOnlySet<string>> privilegesByTable,
        IReadOnlySet<string> schemasWithUsage)
    {
        var requirements = tables
            .Select(table => Required(
                table.FullName,
                [],
                FollowUpImportPolicyPermissions.GetRequiredColumnPrivileges(table.ImportPolicy)))
            .ToArray();
        var existingTables = requirements.ToDictionary(
            item => item.Table,
            _ => (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        return Evaluate(
            existingTables,
            privilegesByTable,
            schemasWithUsage,
            vectorInstalled: true,
            requirements,
            checkVector: false);
    }

    public static async Task<IReadOnlyList<string>> CheckPackageAccessAsync(
        string connectionString,
        IReadOnlyCollection<ExternalCubePackageTable> tables,
        CancellationToken cancellationToken)
    {
        var requirements = tables
            .Select(table => Required(
                table.FullName,
                [],
                FollowUpImportPolicyPermissions.GetRequiredColumnPrivileges(table.ImportPolicy)))
            .ToArray();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
        var privileges = await ReadPrivilegesAsync(connection, transaction, requirements, cancellationToken);
        var schemas = requirements.Select(item => item.Schema).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var schemasWithUsage = await ReadSchemasWithUsageAsync(connection, transaction, schemas, cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return EvaluatePackageAccess(tables, privileges, schemasWithUsage);
    }

    private static ExternalCubeTableRequirement Required(string table, string[] columns, string[] privileges)
    {
        // Upsert SQL 会在 DO UPDATE 中读取 EXCLUDED 列；PostgreSQL 同时要求这些列的 SELECT 权限。
        var requiredPrivileges = privileges.Contains("INSERT", StringComparer.OrdinalIgnoreCase)
                                 && privileges.Contains("UPDATE", StringComparer.OrdinalIgnoreCase)
            ? privileges.Append("SELECT").Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : privileges;
        return new(table, columns, requiredPrivileges);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>> ReadColumnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyCollection<string> schemas,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT table_schema, table_name, column_name
            FROM information_schema.columns
            WHERE table_schema = ANY(@schemas)
            ORDER BY table_schema, table_name, ordinal_position;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<string[]>("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = schemas.ToArray()
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = $"{reader.GetString(0)}.{reader.GetString(1)}";
            if (!result.TryGetValue(table, out var columns))
            {
                columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[table] = columns;
            }
            columns.Add(reader.GetString(2));
        }

        return result.ToDictionary(
            item => item.Key,
            item => (IReadOnlySet<string>)item.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<bool> HasVectorExtensionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_extension extension
                INNER JOIN pg_namespace namespace ON namespace.oid = extension.extnamespace
                WHERE extension.extname = 'vector' AND namespace.nspname = 'form');
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>> ReadPrivilegesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyCollection<ExternalCubeTableRequirement> requirements,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT required.schema_name || '.' || required.table_name, required_privilege.privilege
            FROM unnest(@schemas, @table_names) AS required(schema_name, table_name)
            CROSS JOIN unnest(@privileges) AS required_privilege(privilege)
            WHERE CASE
                WHEN to_regclass(format('%I.%I', required.schema_name, required.table_name)) IS NULL THEN FALSE
                ELSE has_table_privilege(
                    current_user,
                    to_regclass(format('%I.%I', required.schema_name, required.table_name)),
                    required_privilege.privilege)
            END;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var distinctRequirements = requirements
            .DistinctBy(item => item.Table, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        command.Parameters.Add(new NpgsqlParameter<string[]>("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = distinctRequirements.Select(item => item.Schema).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter<string[]>("table_names", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = distinctRequirements.Select(item => item.TableName).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter<string[]>("privileges", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = requirements.SelectMany(item => item.Privileges).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString(0);
            if (!result.TryGetValue(table, out var privileges))
            {
                privileges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[table] = privileges;
            }
            privileges.Add(reader.GetString(1));
        }

        return result.ToDictionary(
            item => item.Key,
            item => (IReadOnlySet<string>)item.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlySet<string>> ReadSchemasWithUsageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyCollection<string> schemas,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT schema_name
            FROM unnest(@schemas) AS required_schema(schema_name)
            WHERE has_schema_privilege(current_user, schema_name, 'USAGE');
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<string[]>("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = schemas.ToArray()
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<DynamicTableReadResult> ReadReferencedDynamicTablesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        const string sql = """
            SELECT DISTINCT BTRIM(table_name)
            FROM form.form_question
            WHERE NULLIF(BTRIM(table_name), '') IS NOT NULL
            ORDER BY 1;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        var issues = new List<string>();
        while (await reader.ReadAsync())
        {
            var table = reader.GetString(0);
            if (IsSafeIdentifier(table))
                tables.Add(table);
            else
                issues.Add($"form.form_question 引用了非法动态表名：{table}");
        }
        return new(tables, issues);
    }

    private static async Task<IReadOnlyList<string>> ReadBackupPrivilegeIssuesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        const string sql = """
            SELECT CASE
                WHEN object_kind = 'schema'
                    THEN format('完整备份账号缺少 schema 使用权限 %I:USAGE', schema_name)
                ELSE format('完整备份账号缺少读取权限 %I.%I:SELECT', schema_name, object_name)
            END
            FROM (
                SELECT 'schema'::text AS object_kind, namespace.nspname AS schema_name, NULL::text AS object_name
                FROM pg_namespace namespace
                WHERE namespace.nspname <> 'information_schema'
                  AND namespace.nspname NOT LIKE 'pg_%'
                  AND NOT has_schema_privilege(current_user, namespace.oid, 'USAGE')

                UNION ALL

                SELECT 'relation', namespace.nspname, relation.relname
                FROM pg_class relation
                INNER JOIN pg_namespace namespace ON namespace.oid = relation.relnamespace
                WHERE namespace.nspname <> 'information_schema'
                  AND namespace.nspname NOT LIKE 'pg_%'
                  AND relation.relkind IN ('r', 'p', 'm')
                  AND NOT has_table_privilege(current_user, relation.oid, 'SELECT')

                UNION ALL

                SELECT 'sequence', namespace.nspname, sequence_relation.relname
                FROM pg_class sequence_relation
                INNER JOIN pg_namespace namespace ON namespace.oid = sequence_relation.relnamespace
                WHERE namespace.nspname <> 'information_schema'
                  AND namespace.nspname NOT LIKE 'pg_%'
                  AND sequence_relation.relkind = 'S'
                  AND NOT has_sequence_privilege(current_user, sequence_relation.oid, 'SELECT')
            ) missing
            ORDER BY schema_name, object_name NULLS FIRST;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        var issues = new List<string>();
        while (await reader.ReadAsync()) issues.Add(reader.GetString(0));
        return issues;
    }

    private static bool IsSafeIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value[0] == '_' || char.IsAsciiLetter(value[0]))
        && value.Skip(1).All(character => character == '_' || char.IsAsciiLetterOrDigit(character));

    private sealed record DynamicTableReadResult(IReadOnlyList<string> TableNames, IReadOnlyList<string> Issues);
}

public sealed record ExternalCubeTableRequirement(
    string Table,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> Privileges)
{
    public string Schema => Table[..Table.IndexOf('.')];
    public string TableName => Table[(Table.IndexOf('.') + 1)..];
}

public sealed record ExternalCubePackageTable(string Schema, string Table, string ImportPolicy)
{
    public string FullName => $"{Schema}.{Table}";
}
