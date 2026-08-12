using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using System.Text;

namespace DataSync.LHYY.V2.Tools;

internal sealed record LegacyFollowUpPatientIdentityMapping(
    Guid SourcePatientId,
    Guid TargetPatientId,
    Guid? SourceUniquePatientId,
    Guid? TargetUniquePatientId,
    string MatchBasis,
    string? OriginalSourceType,
    string FirstPackageId,
    string LastPackageId);

public static class FollowUpPatientIdentityBootstrapTool
{
    private const string CommandName = "followup-patient-map";
    private const string ConfirmFlag = "--confirm-datasync-write";
    private static readonly string[] DataSyncRequiredColumns =
    [
        "hospital_code", "source_patient_id", "target_patient_id",
        "source_unique_patient_id", "target_unique_patient_id",
        "identity_match_basis", "original_source_type",
        "first_package_id", "last_package_id"
    ];

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var hospitalCode = ReadOption(args, "--hospital-code");
            if (args.Length < 2
                || !string.Equals(args[1], "bootstrap", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(hospitalCode)
                || !args.Contains(ConfirmFlag, StringComparer.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"用法：{CommandName} bootstrap --hospital-code <医院编码> {ConfirmFlag}");
                return 1;
            }

            var configuration = new ConfigurationBuilder()
                .SetBasePath(Environment.CurrentDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables()
                .Build();
            var cubeConnectionString = configuration.GetConnectionString("CubeDb")
                ?? throw new InvalidOperationException("未找到连接字符串 'CubeDb'。");
            var dataSyncConnectionString = configuration.GetConnectionString("DataSyncDb")
                ?? throw new InvalidOperationException("未找到连接字符串 'DataSyncDb'。");

            await EnsureDataSyncReadyAsync(dataSyncConnectionString);
            var importedCount = await CountImportedPackagesAsync(dataSyncConnectionString, hospitalCode);
            var mappings = await ReadLegacyMappingsAsync(cubeConnectionString, hospitalCode);
            if (mappings is null || mappings.Count == 0)
            {
                if (importedCount > 0)
                {
                    Console.Error.WriteLine(
                        $"医院 {hospitalCode} 已有 {importedCount} 个历史导入包，但 CubeDb 没有可迁移的旧映射；请在升级后执行 RecoveryBaseline。");
                    return 2;
                }

                Console.WriteLine($"医院 {hospitalCode} 尚无历史导入映射，无需迁移；后续 Baseline 将自动建立映射。");
                return 0;
            }

            ValidateMappings(mappings);
            await WriteMappingsAsync(dataSyncConnectionString, hospitalCode, mappings);
            Console.WriteLine(
                $"医院 {hospitalCode} 的 {mappings.Count} 条旧版 FollowUp 患者映射已幂等迁移到 DataSyncDb；CubeDb 未执行任何写入。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"旧版 FollowUp 患者映射迁移失败：{ex.Message}");
            return 1;
        }
    }

    internal static string BuildLegacyReadSql(IReadOnlySet<string> columns)
    {
        var sourceColumn = columns.Contains("source_patient_id") ? "source_patient_id" : "patient_id";
        var targetColumn = columns.Contains("target_patient_id") ? "target_patient_id" : "patient_id";
        var sourceUnique = columns.Contains("source_unique_patient_id")
            ? "source_map.source_unique_patient_id"
            : "patient.unique_id";
        var targetUnique = columns.Contains("target_unique_patient_id")
            ? "COALESCE(source_map.target_unique_patient_id, patient.unique_id)"
            : "patient.unique_id";
        var matchBasis = columns.Contains("identity_match_basis")
            ? "COALESCE(NULLIF(source_map.identity_match_basis, ''), 'Id')"
            : "'Id'";
        return $"""
            SELECT source_map.{sourceColumn},
                   source_map.{targetColumn},
                   {sourceUnique},
                   {targetUnique},
                   {matchBasis},
                   source_map.original_source_type,
                   source_map.first_package_id,
                   source_map.last_package_id
            FROM datasync.followup_patient_source_map AS source_map
            INNER JOIN public.patient AS patient
                ON patient.id = source_map.{targetColumn}
            WHERE source_map.hospital_code = @hospitalCode
            ORDER BY source_map.{sourceColumn}
            """;
    }

    internal static void ValidateMappings(IReadOnlyCollection<LegacyFollowUpPatientIdentityMapping> mappings)
    {
        if (mappings.GroupBy(item => item.SourcePatientId).Any(group => group.Count() > 1))
            throw new InvalidOperationException("旧版映射中同一 source_patient_id 存在多条记录。");
        if (mappings.GroupBy(item => item.TargetPatientId).Any(group => group.Count() > 1))
            throw new InvalidOperationException("旧版映射中多个来源患者指向同一 target_patient_id。");
        if (mappings.Any(item => item.MatchBasis is not ("Id" or "SidNumber" or "Demographics")))
            throw new InvalidOperationException("旧版映射包含未知 identity_match_basis。");
        if (mappings.Any(item => string.IsNullOrWhiteSpace(item.FirstPackageId)
                                 || string.IsNullOrWhiteSpace(item.LastPackageId)))
            throw new InvalidOperationException("旧版映射缺少首次或最近包 ID。");
    }

    internal static string BuildUpsertSql() => """
        INSERT INTO lhyy.followup_patient_identity_map
            (hospital_code, source_patient_id, target_patient_id,
             source_unique_patient_id, target_unique_patient_id,
             identity_match_basis, original_source_type,
             first_package_id, last_package_id, created_at, updated_at)
        SELECT @hospitalCode,
               source.source_patient_id,
               source.target_patient_id,
               source.source_unique_patient_id,
               source.target_unique_patient_id,
               source.identity_match_basis,
               source.original_source_type,
               source.first_package_id,
               source.last_package_id,
               now(),
               now()
        FROM unnest(
            @sourcePatientIds,
            @targetPatientIds,
            @sourceUniquePatientIds,
            @targetUniquePatientIds,
            @matchBases,
            @originalSourceTypes,
            @firstPackageIds,
            @lastPackageIds)
            AS source(
                source_patient_id,
                target_patient_id,
                source_unique_patient_id,
                target_unique_patient_id,
                identity_match_basis,
                original_source_type,
                first_package_id,
                last_package_id)
        ON CONFLICT (hospital_code, source_patient_id) DO UPDATE SET
            source_unique_patient_id = COALESCE(
                lhyy.followup_patient_identity_map.source_unique_patient_id,
                EXCLUDED.source_unique_patient_id),
            target_unique_patient_id = COALESCE(
                lhyy.followup_patient_identity_map.target_unique_patient_id,
                EXCLUDED.target_unique_patient_id),
            original_source_type = COALESCE(
                lhyy.followup_patient_identity_map.original_source_type,
                EXCLUDED.original_source_type),
            updated_at = now()
        WHERE lhyy.followup_patient_identity_map.target_patient_id = EXCLUDED.target_patient_id
          AND lhyy.followup_patient_identity_map.first_package_id = EXCLUDED.first_package_id
          AND lhyy.followup_patient_identity_map.last_package_id = EXCLUDED.last_package_id
          AND lhyy.followup_patient_identity_map.identity_match_basis = EXCLUDED.identity_match_basis
          AND (
              lhyy.followup_patient_identity_map.source_unique_patient_id IS NULL
              OR EXCLUDED.source_unique_patient_id IS NULL
              OR lhyy.followup_patient_identity_map.source_unique_patient_id = EXCLUDED.source_unique_patient_id)
          AND (
              lhyy.followup_patient_identity_map.target_unique_patient_id IS NULL
              OR EXCLUDED.target_unique_patient_id IS NULL
              OR lhyy.followup_patient_identity_map.target_unique_patient_id = EXCLUDED.target_unique_patient_id)
        """;

    private static async Task<IReadOnlyList<LegacyFollowUpPatientIdentityMapping>?> ReadLegacyMappingsAsync(
        string connectionString,
        string hospitalCode)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync();

        await using var columnsCommand = new NpgsqlCommand("""
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'datasync'
              AND table_name = 'followup_patient_source_map'
            """, connection, transaction);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await columnsCommand.ExecuteReaderAsync())
            while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        if (columns.Count == 0)
        {
            await transaction.RollbackAsync();
            return null;
        }

        var required = new[]
        {
            "hospital_code", "original_source_type", "first_package_id", "last_package_id"
        };
        var missing = required.Where(column => !columns.Contains(column)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"CubeDb 旧版映射表结构不完整：{string.Join(", ", missing)}。");
        if (!columns.Contains("source_patient_id") && !columns.Contains("patient_id"))
            throw new InvalidOperationException("CubeDb 旧版映射表缺少来源患者 ID 字段。");
        if (!columns.Contains("target_patient_id") && !columns.Contains("patient_id"))
            throw new InvalidOperationException("CubeDb 旧版映射表缺少目标患者 ID 字段。");

        await using var command = new NpgsqlCommand(BuildLegacyReadSql(columns), connection, transaction);
        command.Parameters.AddWithValue("hospitalCode", hospitalCode);
        var result = new List<LegacyFollowUpPatientIdentityMapping>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                result.Add(new LegacyFollowUpPatientIdentityMapping(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7)));
            }
        }
        await transaction.RollbackAsync();
        return result;
    }

    private static async Task EnsureDataSyncReadyAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'lhyy'
              AND table_name = 'followup_patient_identity_map'
            """, connection);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        var missing = DataSyncRequiredColumns.Where(column => !columns.Contains(column)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                $"DataSyncDb 尚未执行 20260811.sql，缺少字段：{string.Join(", ", missing)}。");
    }

    private static async Task<long> CountImportedPackagesAsync(string connectionString, string hospitalCode)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*)
            FROM lhyy.followup_package_import_state
            WHERE hospital_code = @hospitalCode
              AND import_status IN ('Imported', 'Importing', 'Restoring', 'RestoreFailed')
            """, connection);
        command.Parameters.AddWithValue("hospitalCode", hospitalCode);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task WriteMappingsAsync(
        string connectionString,
        string hospitalCode,
        IReadOnlyCollection<LegacyFollowUpPatientIdentityMapping> mappings)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(BuildUpsertSql(), connection, transaction);
        command.Parameters.AddWithValue("hospitalCode", hospitalCode);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("sourcePatientIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = mappings.Select(item => item.SourcePatientId).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("targetPatientIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = mappings.Select(item => item.TargetPatientId).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter("sourceUniquePatientIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            Value = mappings.Select(item => item.SourceUniquePatientId).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter("targetUniquePatientIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            Value = mappings.Select(item => item.TargetUniquePatientId).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter<string[]>("matchBases", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = mappings.Select(item => item.MatchBasis).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter<string?[]>("originalSourceTypes", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = mappings.Select(item => item.OriginalSourceType).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter<string[]>("firstPackageIds", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = mappings.Select(item => item.FirstPackageId).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter<string[]>("lastPackageIds", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = mappings.Select(item => item.LastPackageId).ToArray()
        });
        var affected = await command.ExecuteNonQueryAsync();
        if (affected != mappings.Count)
            throw new InvalidOperationException("DataSyncDb 已有映射与旧版映射不一致，事务已回滚。");
        await transaction.CommitAsync();
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        return null;
    }
}
