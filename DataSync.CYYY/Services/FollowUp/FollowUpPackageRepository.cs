using DataSync.CYYY.Models.FollowUp;
using DataSync.Common.FollowUp;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

namespace DataSync.CYYY.Services.FollowUp;

public sealed class FollowUpPackageRepository(IConfiguration configuration)
{
    // TODO: EF model sync 后改为 SyncDbContext DbSet 访问。
    private static readonly string[] RequiredTables =
    [
        "followup_package_source_config", "followup_package_pull_state",
        "followup_package_pull_log", "followup_package_ack_queue"
    ];

    private readonly string _connectionString = configuration.GetConnectionString("SyncDb")
        ?? throw new InvalidOperationException("未找到连接字符串 'SyncDb'");

    public async Task<List<string>> GetMissingTablesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'cyyy' AND table_name = ANY(@tables)
            """, connection);
        command.Parameters.AddWithValue("tables", RequiredTables);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(0));
        return RequiredTables.Where(table => !existing.Contains(table)).ToList();
    }

    public async Task<List<FollowUpPackageSourceConfig>> GetSourcesAsync(bool enabledOnly, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT id, hospital_code, hospital_name, is_enabled, dmz_host, dmz_port, dmz_user,
                   package_root, pull_interval_seconds, pull_policy_json::text, security_json::text
            FROM cyyy.followup_package_source_config
            {(enabledOnly ? "WHERE is_enabled = true" : string.Empty)}
            ORDER BY hospital_code
            """, connection);
        var result = new List<FollowUpPackageSourceConfig>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FollowUpPackageSourceConfig
            {
                Id = reader.GetGuid(0),
                HospitalCode = reader.GetString(1),
                HospitalName = reader.GetString(2),
                IsEnabled = reader.GetBoolean(3),
                DmzHost = reader.GetString(4),
                DmzPort = reader.GetInt32(5),
                DmzUser = reader.GetString(6),
                PackageRoot = reader.GetString(7),
                PullIntervalSeconds = reader.GetInt32(8),
                PullPolicyJson = reader.GetString(9),
                SecurityJson = reader.GetString(10)
            });
        }
        return result;
    }

    public async Task SaveSourceAsync(FollowUpPackageSourceConfig source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.HospitalCode)) throw new InvalidOperationException("医院编码不能为空。");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO cyyy.followup_package_source_config
                (hospital_code, hospital_name, is_enabled, dmz_host, dmz_port, dmz_user,
                 package_root, pull_interval_seconds, pull_policy_json, security_json, updated_at)
            VALUES
                (@hospitalCode, @hospitalName, @enabled, @host, @port, @user,
                 @root, @interval, @pullPolicy::jsonb, @security::jsonb, now())
            ON CONFLICT (hospital_code) DO UPDATE SET
                hospital_name = EXCLUDED.hospital_name,
                is_enabled = EXCLUDED.is_enabled,
                dmz_host = EXCLUDED.dmz_host,
                dmz_port = EXCLUDED.dmz_port,
                dmz_user = EXCLUDED.dmz_user,
                package_root = EXCLUDED.package_root,
                pull_interval_seconds = EXCLUDED.pull_interval_seconds,
                pull_policy_json = EXCLUDED.pull_policy_json,
                security_json = EXCLUDED.security_json,
                updated_at = now()
            """, connection);
        command.Parameters.AddWithValue("hospitalCode", source.HospitalCode.Trim());
        command.Parameters.AddWithValue("hospitalName", source.HospitalName.Trim());
        command.Parameters.AddWithValue("enabled", source.IsEnabled);
        command.Parameters.AddWithValue("host", source.DmzHost.Trim());
        command.Parameters.AddWithValue("port", source.DmzPort);
        command.Parameters.AddWithValue("user", source.DmzUser.Trim());
        command.Parameters.AddWithValue("root", source.PackageRoot.Trim());
        command.Parameters.AddWithValue("interval", Math.Clamp(source.PullIntervalSeconds, 30, 86400));
        command.Parameters.AddWithValue("pullPolicy", NormalizeJson(source.PullPolicyJson));
        command.Parameters.AddWithValue("security", NormalizeJson(source.SecurityJson));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long?> GetMaxSequenceNoAsync(string hospitalCode, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT max(sequence_no) FROM cyyy.followup_package_pull_state WHERE hospital_code = @hospitalCode
            """, connection);
        command.Parameters.AddWithValue("hospitalCode", hospitalCode);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    public async Task<List<FollowUpPackageSummary>> GetRetryPackagesAsync(
        string hospitalCode,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT package_summary_json::text
            FROM cyyy.followup_package_pull_state
            WHERE hospital_code = @hospitalCode AND pull_status = ANY(@statuses)
            ORDER BY sequence_no
            """, connection);
        command.Parameters.AddWithValue("hospitalCode", hospitalCode);
        command.Parameters.AddWithValue("statuses", FollowUpPackageRetryPolicy.DatabaseStatuses);
        var result = new List<FollowUpPackageSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var package = JsonSerializer.Deserialize<FollowUpPackageSummary>(reader.GetString(0), FollowUpJson.Options)
                ?? throw new InvalidDataException("本地待重试包摘要为空。");
            result.Add(package);
        }
        return result;
    }

    public async Task UpsertPackageSummaryAsync(
        string hospitalCode,
        FollowUpPackageSummary package,
        CancellationToken cancellationToken = default)
    {
        var summaryJson = JsonSerializer.Serialize(package, FollowUpJson.Options);
        var schemaJson = JsonSerializer.Serialize(new
        {
            schemaDiffLevel = package.SchemaDiffLevel,
            package.RequiresSchemaReview
        }, FollowUpJson.Options);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO cyyy.followup_package_pull_state
                (hospital_code, package_id, sequence_no, package_type, trigger_type, pull_status,
                 from_watermark, to_watermark, previous_package_id, package_hash, size_bytes,
                 schema_summary_json, package_summary_json, updated_at)
            VALUES
                (@hospitalCode, @packageId, @sequenceNo, @packageType, @triggerType, 'Pending',
                 @fromWatermark, @toWatermark, @previousPackageId, @packageHash, @sizeBytes,
                 @schema::jsonb, @summary::jsonb, now())
            ON CONFLICT (hospital_code, package_id) DO UPDATE SET
                package_type = EXCLUDED.package_type,
                trigger_type = EXCLUDED.trigger_type,
                from_watermark = EXCLUDED.from_watermark,
                to_watermark = EXCLUDED.to_watermark,
                previous_package_id = EXCLUDED.previous_package_id,
                package_hash = EXCLUDED.package_hash,
                size_bytes = EXCLUDED.size_bytes,
                schema_summary_json = EXCLUDED.schema_summary_json,
                package_summary_json = EXCLUDED.package_summary_json,
                updated_at = now()
            """, connection);
        command.Parameters.AddWithValue("hospitalCode", hospitalCode);
        command.Parameters.AddWithValue("packageId", package.PackageId);
        command.Parameters.AddWithValue("sequenceNo", package.SequenceNo);
        command.Parameters.AddWithValue("packageType", package.PackageType);
        command.Parameters.AddWithValue("triggerType", package.TriggerType);
        command.Parameters.Add(new NpgsqlParameter("fromWatermark", NpgsqlDbType.Timestamp) { Value = DbValue(package.FromWatermark) });
        command.Parameters.Add(new NpgsqlParameter("toWatermark", NpgsqlDbType.Timestamp) { Value = DbValue(package.ToWatermark) });
        command.Parameters.Add(new NpgsqlParameter("previousPackageId", NpgsqlDbType.Text) { Value = DbValue(package.PreviousPackageId) });
        command.Parameters.Add(new NpgsqlParameter("packageHash", NpgsqlDbType.Text) { Value = DbValue(package.PackageHash) });
        command.Parameters.AddWithValue("sizeBytes", package.SizeBytes);
        command.Parameters.AddWithValue("schema", schemaJson);
        command.Parameters.AddWithValue("summary", summaryJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkPackageAsync(
        string hospitalCode,
        string packageId,
        string status,
        string? localPath,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE cyyy.followup_package_pull_state SET
                pull_status = @status,
                local_package_path = COALESCE(@localPath, local_package_path),
                error_code = @errorCode,
                error_message = @errorMessage,
                retry_count = CASE WHEN @status = 'Failed' THEN retry_count + 1 ELSE retry_count END,
                first_pulled_at = CASE WHEN @status = 'Pulled' THEN COALESCE(first_pulled_at, now()) ELSE first_pulled_at END,
                last_pulled_at = CASE WHEN @status = 'Pulled' THEN now() ELSE last_pulled_at END,
                updated_at = now()
            WHERE hospital_code = @hospitalCode AND package_id = @packageId
            """, connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.Add(new NpgsqlParameter("localPath", NpgsqlDbType.Text) { Value = DbValue(localPath) });
        command.Parameters.Add(new NpgsqlParameter("errorCode", NpgsqlDbType.Text) { Value = DbValue(errorCode) });
        command.Parameters.Add(new NpgsqlParameter("errorMessage", NpgsqlDbType.Text) { Value = DbValue(Truncate(errorMessage, 1000)) });
        command.Parameters.AddWithValue("hospitalCode", hospitalCode);
        command.Parameters.AddWithValue("packageId", packageId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<FollowUpPackagePullState>> GetPackagesAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, hospital_code, package_id, sequence_no, package_type, trigger_type, pull_status,
                   from_watermark, to_watermark, previous_package_id, package_hash, size_bytes,
                   local_package_path, schema_summary_json::text, package_summary_json::text,
                   error_code, error_message, retry_count, last_pulled_at
            FROM cyyy.followup_package_pull_state
            ORDER BY sequence_no DESC LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1000));
        var result = new List<FollowUpPackagePullState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FollowUpPackagePullState
            {
                Id = reader.GetGuid(0), HospitalCode = reader.GetString(1), PackageId = reader.GetString(2),
                SequenceNo = reader.GetInt64(3), PackageType = reader.GetString(4), TriggerType = reader.GetString(5),
                PullStatus = reader.GetString(6), FromWatermark = ReadDateTime(reader, 7), ToWatermark = ReadDateTime(reader, 8),
                PreviousPackageId = ReadString(reader, 9), PackageHash = ReadString(reader, 10), SizeBytes = reader.GetInt64(11),
                LocalPackagePath = reader.GetString(12), SchemaSummaryJson = reader.GetString(13), PackageSummaryJson = reader.GetString(14),
                ErrorCode = ReadString(reader, 15), ErrorMessage = ReadString(reader, 16), RetryCount = reader.GetInt32(17),
                LastPulledAt = ReadDateTime(reader, 18)
            });
        }
        return result;
    }

    public async Task<List<FollowUpPackageAckQueueItem>> GetAcksAsync(bool pendingOnly, int limit = 200, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT id, hospital_code, package_id, ack_status, ack_payload_json::text, forward_status, retry_count
            FROM cyyy.followup_package_ack_queue
            {(pendingOnly ? "WHERE forward_status IN ('Pending', 'Failed') AND (next_retry_at IS NULL OR next_retry_at <= now())" : string.Empty)}
            ORDER BY created_at LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1000));
        var result = new List<FollowUpPackageAckQueueItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FollowUpPackageAckQueueItem
            {
                Id = reader.GetGuid(0), HospitalCode = reader.GetString(1), PackageId = reader.GetString(2),
                AckStatus = reader.GetString(3), AckPayloadJson = reader.GetString(4),
                ForwardStatus = reader.GetString(5), RetryCount = reader.GetInt32(6)
            });
        }
        return result;
    }

    public async Task MarkAckAsync(Guid id, bool success, string? errorCode, string? errorMessage, int retrySeconds, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE cyyy.followup_package_ack_queue SET
                forward_status = CASE WHEN @success THEN 'Forwarded' ELSE 'Failed' END,
                forward_error_code = @errorCode,
                forward_error_message = @errorMessage,
                retry_count = CASE WHEN @success THEN retry_count ELSE retry_count + 1 END,
                next_retry_at = CASE WHEN @success THEN NULL ELSE now() + make_interval(secs => @retrySeconds) END,
                forwarded_at = CASE WHEN @success THEN now() ELSE forwarded_at END,
                updated_at = now()
            WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("success", success);
        command.Parameters.Add(new NpgsqlParameter("errorCode", NpgsqlDbType.Text) { Value = DbValue(errorCode) });
        command.Parameters.Add(new NpgsqlParameter("errorMessage", NpgsqlDbType.Text) { Value = DbValue(Truncate(errorMessage, 1000)) });
        command.Parameters.AddWithValue("retrySeconds", Math.Clamp(retrySeconds, 10, 86400));
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddLogAsync(string? hospitalCode, string? packageId, string operation, string level, string message, object? detail, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO cyyy.followup_package_pull_log
                (hospital_code, package_id, operation, level, message, detail_json)
            VALUES (@hospitalCode, @packageId, @operation, @level, @message, @detail::jsonb)
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("hospitalCode", NpgsqlDbType.Text) { Value = DbValue(hospitalCode) });
        command.Parameters.Add(new NpgsqlParameter("packageId", NpgsqlDbType.Text) { Value = DbValue(packageId) });
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("level", level);
        command.Parameters.AddWithValue("message", Truncate(message, 1000) ?? string.Empty);
        command.Parameters.AddWithValue("detail", JsonSerializer.Serialize(detail ?? new { }, FollowUpJson.Options));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string NormalizeJson(string value)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(value) ? "{}" : value);
        return document.RootElement.GetRawText();
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;
    private static string? ReadString(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTime? ReadDateTime(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    private static string? Truncate(string? value, int max) => value is { Length: > 0 } ? value[..Math.Min(value.Length, max)] : value;
}
