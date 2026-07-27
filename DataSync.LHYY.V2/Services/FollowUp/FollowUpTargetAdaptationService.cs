using DataSync.Common.FollowUp;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DataSync.LHYY.V2.Services.FollowUp;

internal sealed record FollowUpPatientSource(Guid PatientId, string? OriginalSourceType);

/// <summary>
/// 将 FollowUp 数据包中的患者来源适配为 NTCare 现有查询约定，并在 CubeDb 中保留原始来源标识。
/// 患者事件的表单资格由云端导出范围统一判定，医院端不再改写事件字段。
/// 数据包、staging 文件和导入前备份均不经过此层修改。
/// </summary>
public sealed class FollowUpTargetAdaptationService(IConfiguration configuration)
{
    private static readonly string[] RequiredSourceMapColumns =
    [
        "patient_id", "original_source_type", "hospital_code", "first_package_id", "last_package_id",
        "created_at", "updated_at"
    ];

    private readonly string _cubeConnectionString = configuration.GetConnectionString("CubeDb")
        ?? throw new InvalidOperationException("未找到连接字符串 'CubeDb'");

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_cubeConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'datasync'
              AND table_name = 'followup_patient_source_map'
            """, connection);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(0));

        var missing = RequiredSourceMapColumns.Where(column => !columns.Contains(column)).ToArray();
        if (missing.Length > 0)
        {
            throw new FollowUpPackageException(
                FollowUpErrorCodes.SchemaReviewRequired,
                $"CubeDb 缺少 FollowUp 来源映射结构 datasync.followup_patient_source_map 或字段：{string.Join(", ", missing)}。请先执行 CubeDb 专用迁移。");
        }
    }

    internal async Task<int> ApplySourceMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyCollection<FollowUpPatientSource> patients,
        string hospitalCode,
        string packageId,
        CancellationToken cancellationToken)
    {
        if (patients.Count == 0)
            return 0;

        var distinctPatients = patients
            .GroupBy(patient => patient.PatientId)
            .Select(group => group.Last())
            .ToArray();

        await using var command = new NpgsqlCommand(BuildSourceMapUpsertSql(), connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("patient_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = distinctPatients.Select(patient => patient.PatientId).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter<string?[]>("original_source_types", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = distinctPatients.Select(patient => patient.OriginalSourceType).ToArray()
        });
        command.Parameters.AddWithValue("hospital_code", hospitalCode);
        command.Parameters.AddWithValue("package_id", packageId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static string AdaptRow(string targetSchema, string targetTable, string row)
    {
        if (IsPatientTable(targetSchema, targetTable))
        {
            var patient = ParseObject(row, "public.patient");
            patient["source_type"] = "care";
            return patient.ToJsonString(FollowUpJson.Options);
        }

        return row;
    }

    internal static FollowUpPatientSource? ReadPatientSource(
        string targetSchema,
        string targetTable,
        string row)
    {
        if (!IsPatientTable(targetSchema, targetTable))
            return null;

        using var document = JsonDocument.Parse(row);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var id) || !Guid.TryParse(id.GetString(), out var patientId))
            return null;
        var originalSourceType = root.TryGetProperty("source_type", out var sourceType)
                                 && sourceType.ValueKind == JsonValueKind.String
            ? sourceType.GetString()
            : null;
        return new FollowUpPatientSource(patientId, originalSourceType);
    }

    internal static string BuildSourceMapUpsertSql() => """
        INSERT INTO datasync.followup_patient_source_map
            (patient_id, original_source_type, hospital_code, first_package_id, last_package_id, created_at, updated_at)
        SELECT source.patient_id, source.original_source_type, @hospital_code, @package_id, @package_id,
               CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
        FROM unnest(@patient_ids, @original_source_types) AS source(patient_id, original_source_type)
        ON CONFLICT (patient_id) DO UPDATE SET
            original_source_type = EXCLUDED.original_source_type,
            hospital_code = EXCLUDED.hospital_code,
            last_package_id = EXCLUDED.last_package_id,
            updated_at = CURRENT_TIMESTAMP
        """;

    private static bool IsPatientTable(string schema, string table) =>
        schema.Equals("public", StringComparison.OrdinalIgnoreCase)
        && table.Equals("patient", StringComparison.OrdinalIgnoreCase);

    private static JsonObject ParseObject(string row, string table) =>
        JsonNode.Parse(row)?.AsObject()
        ?? throw new InvalidDataException($"{table} 数据行不是 JSON 对象。");
}
