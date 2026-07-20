using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

namespace DataSync.LHYY.V2.Services.FollowUp;

public sealed class FollowUpPackageImportRepository(
    IConfiguration configuration,
    FollowUpCubeOperationCoordinator operationCoordinator) : IFollowUpRestoreCompletionReconciler
{
    // TODO: EF model sync 后改为 DataSyncDbContext DbSet 访问。
    private static readonly string[] RequiredTables =
    [
        "followup_package_import_state", "followup_package_import_log", "followup_package_backup_record",
        "followup_package_schema_check", "followup_package_restore_record"
    ];
    private static readonly string[] RediscoverableImportStatuses =
    [
        "AwaitingPackage", "WaitingForPredecessor"
    ];
    private static readonly string[] UnsafeOperationStatuses = ["RestoreFailed", "Restoring", "Importing"];
    internal static readonly string[] RestorableImportStatuses = ["Imported", "RestoreFailed", "Importing", "Restoring"];
    private readonly string _connectionString = configuration.GetConnectionString("DataSyncDb")
        ?? throw new InvalidOperationException("未找到连接字符串 'DataSyncDb'");

    public async Task<List<string>> GetMissingTablesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = 'lhyy' AND table_name = ANY(@tables)
            """, connection);
        command.Parameters.AddWithValue("tables", RequiredTables);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(0));
        return RequiredTables.Where(item => !existing.Contains(item)).ToList();
    }

    public async Task DiscoverAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var discovered = new List<FollowUpDiscoveredPackage>();
        await using (var command = new NpgsqlCommand("""
            SELECT hospital_code, package_id, sequence_no, package_type, from_watermark, to_watermark,
                   previous_package_id, package_hash, local_package_path, pull_status
            FROM cyyy.followup_package_pull_state
            ORDER BY hospital_code, sequence_no
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                discovered.Add(new FollowUpDiscoveredPackage
                {
                    HospitalCode = reader.GetString(0), PackageId = reader.GetString(1), SequenceNo = reader.GetInt64(2),
                    PackageType = reader.GetString(3), FromWatermark = ReadDateTime(reader, 4), ToWatermark = ReadDateTime(reader, 5),
                    PreviousPackageId = ReadString(reader, 6), PackageHash = ReadString(reader, 7),
                    LocalPackagePath = reader.GetString(8), PullStatus = reader.GetString(9)
                });
            }
        }
        foreach (var package in discovered)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO lhyy.followup_package_import_state
                    (hospital_code, package_id, sequence_no, package_type, import_status,
                     from_watermark, to_watermark, previous_package_id, package_hash, local_package_path,
                     import_summary_json, updated_at)
                VALUES
                    (@hospitalCode, @packageId, @sequenceNo, @packageType, @status,
                     @fromWatermark, @toWatermark,
                     CASE WHEN @previousPackageId IS NULL OR EXISTS (
                         SELECT 1 FROM lhyy.followup_package_import_state
                         WHERE hospital_code = @hospitalCode AND package_id = @previousPackageId)
                     THEN @previousPackageId ELSE NULL END,
                     @packageHash, @localPath, @summary::jsonb, now())
                ON CONFLICT (hospital_code, package_id) DO UPDATE SET
                    local_package_path = EXCLUDED.local_package_path,
                    package_hash = EXCLUDED.package_hash,
                    import_status = CASE
                        WHEN lhyy.followup_package_import_state.import_status = ANY(@rediscoverableStatuses)
                            THEN EXCLUDED.import_status
                        ELSE lhyy.followup_package_import_state.import_status END,
                    updated_at = now()
                """, connection);
            command.Parameters.AddWithValue("hospitalCode", package.HospitalCode);
            command.Parameters.AddWithValue("packageId", package.PackageId);
            command.Parameters.AddWithValue("sequenceNo", package.SequenceNo);
            command.Parameters.AddWithValue("packageType", package.PackageType);
            command.Parameters.AddWithValue("status", ResolveDiscoveryStatus(null, package.PullStatus));
            command.Parameters.AddWithValue("rediscoverableStatuses", RediscoverableImportStatuses);
            command.Parameters.Add(new NpgsqlParameter("fromWatermark", NpgsqlDbType.Timestamp) { Value = DbValue(package.FromWatermark) });
            command.Parameters.Add(new NpgsqlParameter("toWatermark", NpgsqlDbType.Timestamp) { Value = DbValue(package.ToWatermark) });
            command.Parameters.Add(new NpgsqlParameter("previousPackageId", NpgsqlDbType.Text) { Value = DbValue(package.PreviousPackageId) });
            command.Parameters.Add(new NpgsqlParameter("packageHash", NpgsqlDbType.Text) { Value = DbValue(package.PackageHash) });
            command.Parameters.AddWithValue("localPath", package.LocalPackagePath);
            command.Parameters.AddWithValue("summary", JsonSerializer.Serialize(new { declaredPreviousPackageId = package.PreviousPackageId }, FollowUpJson.Options));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<List<FollowUpPackageImportState>> GetPackagesAsync(int limit = 500, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, hospital_code, package_id, sequence_no, package_type, import_status,
                   previous_package_id, package_hash, local_package_path, staging_path,
                   schema_diff_level, requires_schema_review, error_code, error_message, started_at, finished_at
            FROM lhyy.followup_package_import_state
            ORDER BY hospital_code, sequence_no DESC LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 2000));
        var result = new List<FollowUpPackageImportState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadState(reader));
        return result;
    }

    public async Task<List<FollowUpPackageImportState>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, hospital_code, package_id, sequence_no, package_type, import_status,
                   previous_package_id, package_hash, local_package_path, staging_path,
                   schema_diff_level, requires_schema_review, error_code, error_message, started_at, finished_at
            FROM lhyy.followup_package_import_state
            WHERE import_status = 'Pending'
            ORDER BY hospital_code, sequence_no
            """, connection);
        var result = new List<FollowUpPackageImportState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadState(reader));
        return result;
    }

    public async Task<bool> HasUnsafeOperationAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM lhyy.followup_package_import_state
                WHERE import_status = ANY(@unsafeStatuses))
            """, connection);
        command.Parameters.AddWithValue("unsafeStatuses", UnsafeOperationStatuses);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    internal static string ResolveDiscoveryStatus(string? currentStatus, string pullStatus)
    {
        var discoveredStatus = pullStatus == "Pulled" ? "Pending" : "AwaitingPackage";
        return currentStatus is null || RediscoverableImportStatuses.Contains(currentStatus, StringComparer.Ordinal)
            ? discoveredStatus
            : currentStatus;
    }

    public async Task<string?> GetCurrentMainHeadAsync(string hospitalCode, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT package_id FROM lhyy.followup_package_import_state
            WHERE hospital_code = @hospitalCode AND import_status = 'Imported'
              AND package_type IN ('Baseline', 'Incremental', 'Replacement')
            ORDER BY COALESCE(finished_at, updated_at) DESC, sequence_no DESC LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("hospitalCode", hospitalCode);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<string?> GetCurrentRestorableHeadAsync(string hospitalCode, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT package_id FROM lhyy.followup_package_import_state
            WHERE hospital_code = @hospitalCode AND import_status = ANY(@restorableStatuses)
            ORDER BY COALESCE(finished_at, updated_at) DESC, sequence_no DESC LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("hospitalCode", hospitalCode);
        command.Parameters.AddWithValue("restorableStatuses", RestorableImportStatuses);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<(bool Exists, bool Imported, string? Status)> GetPackageStatusAsync(string hospitalCode, string? packageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return (false, false, null);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT import_status FROM lhyy.followup_package_import_state
            WHERE hospital_code = @hospitalCode AND package_id = @packageId
            """, connection);
        command.Parameters.AddWithValue("hospitalCode", hospitalCode);
        command.Parameters.AddWithValue("packageId", packageId);
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return (value is not null, value == "Imported", value);
    }

    public async Task SaveVerifiedAsync(FollowUpVerifiedPackage package, FollowUpSchemaCheckResult check, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("""
            UPDATE lhyy.followup_package_import_state SET
                previous_package_id = CASE WHEN @previousPackageId IS NULL OR EXISTS (
                    SELECT 1 FROM lhyy.followup_package_import_state parent
                    WHERE parent.hospital_code = @hospitalCode AND parent.package_id = @previousPackageId)
                    THEN @previousPackageId ELSE previous_package_id END,
                staging_path = @stagingPath,
                export_contract_version = @contractVersion,
                min_importer_version = @minImporterVersion,
                importer_version = @importerVersion,
                schema_check_status = @checkStatus,
                schema_diff_level = @diffLevel,
                requires_schema_review = @requiresReview,
                table_manifest_json = @tableManifest::jsonb,
                schema_snapshot_json = @schemaSnapshot::jsonb,
                schema_diff_json = @schemaDiff::jsonb,
                package_hash = @packageHash,
                updated_at = now()
            WHERE hospital_code = @hospitalCode AND package_id = @packageId
            """, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter("previousPackageId", NpgsqlDbType.Text) { Value = DbValue(package.Manifest.PreviousPackageId) });
            command.Parameters.AddWithValue("hospitalCode", package.Manifest.HospitalCode);
            command.Parameters.AddWithValue("packageId", package.Manifest.PackageId);
            command.Parameters.AddWithValue("stagingPath", package.StagingPath);
            command.Parameters.AddWithValue("contractVersion", package.Manifest.ExportContractVersion);
            command.Parameters.AddWithValue("minImporterVersion", package.Manifest.MinImporterVersion);
            command.Parameters.AddWithValue("importerVersion", "1.0.0");
            command.Parameters.AddWithValue("checkStatus", check.Status);
            command.Parameters.AddWithValue("diffLevel", check.DiffLevel);
            command.Parameters.AddWithValue("requiresReview", !check.Compatible);
            command.Parameters.AddWithValue("tableManifest", JsonSerializer.Serialize(package.TableManifest, FollowUpJson.Options));
            command.Parameters.AddWithValue("schemaSnapshot", JsonSerializer.Serialize(package.SchemaSnapshot, FollowUpJson.Options));
            command.Parameters.AddWithValue("schemaDiff", JsonSerializer.Serialize(package.SchemaDiff, FollowUpJson.Options));
            command.Parameters.AddWithValue("packageHash", package.PackageHash);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var command = new NpgsqlCommand("""
            INSERT INTO lhyy.followup_package_schema_check
                (hospital_code, package_id, check_status, export_contract_version, importer_version,
                 schema_diff_level, compatible, check_result_json, restore_plan_json, decision_status, checked_at)
            VALUES (@hospitalCode, @packageId, @status, @contract, @importer, @level, @compatible,
                    @result::jsonb, '{}'::jsonb, @decision, now())
            ON CONFLICT (hospital_code, package_id) DO UPDATE SET
                check_status = EXCLUDED.check_status, export_contract_version = EXCLUDED.export_contract_version,
                importer_version = EXCLUDED.importer_version, schema_diff_level = EXCLUDED.schema_diff_level,
                compatible = EXCLUDED.compatible, check_result_json = EXCLUDED.check_result_json,
                decision_status = CASE
                    WHEN lhyy.followup_package_schema_check.decision_status IN ('ApprovedMapping', 'WaitingForUpgrade')
                        THEN lhyy.followup_package_schema_check.decision_status
                    ELSE EXCLUDED.decision_status END,
                checked_at = now()
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("hospitalCode", package.Manifest.HospitalCode);
            command.Parameters.AddWithValue("packageId", package.Manifest.PackageId);
            command.Parameters.AddWithValue("status", check.Status);
            command.Parameters.AddWithValue("contract", package.Manifest.ExportContractVersion);
            command.Parameters.AddWithValue("importer", "1.0.0");
            command.Parameters.AddWithValue("level", check.DiffLevel);
            command.Parameters.AddWithValue("compatible", check.Compatible);
            command.Parameters.AddWithValue("result", JsonSerializer.Serialize(check, FollowUpJson.Options));
            command.Parameters.AddWithValue("decision", check.Compatible ? "AutoAccepted" : "Pending");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<FollowUpSchemaDecision?> GetSchemaDecisionAsync(
        string hospitalCode,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT decision_json::text
            FROM lhyy.followup_package_schema_check
            WHERE hospital_code = @hospitalCode AND package_id = @packageId
              AND decision_status IN ('ApprovedMapping', 'WaitingForUpgrade')
            """, connection);
        command.Parameters.AddWithValue("hospitalCode", hospitalCode);
        command.Parameters.AddWithValue("packageId", packageId);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<FollowUpSchemaDecision>(json, FollowUpJson.Options);
    }

    public async Task SaveSchemaDecisionAsync(
        string hospitalCode,
        string packageId,
        FollowUpSchemaDecision decision,
        CancellationToken cancellationToken = default)
    {
        if (decision.DecisionStatus is not ("ApprovedMapping" or "WaitingForUpgrade"))
            throw new InvalidOperationException("结构决定只能是 ApprovedMapping 或 WaitingForUpgrade。");
        if (string.IsNullOrWhiteSpace(decision.OperatorName))
            throw new InvalidOperationException("必须填写结构决定操作人。");
        if (decision.DecisionStatus == "ApprovedMapping" && decision.TableMappings.Count == 0)
            throw new InvalidOperationException("ApprovedMapping 至少需要一项表映射。");

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("""
            UPDATE lhyy.followup_package_schema_check SET
                decision_status = @status,
                decision_json = @decision::jsonb,
                decided_at = now(),
                operator_name = @operatorName
            WHERE hospital_code = @hospitalCode AND package_id = @packageId
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("status", decision.DecisionStatus);
            command.Parameters.AddWithValue("decision", JsonSerializer.Serialize(decision, FollowUpJson.Options));
            command.Parameters.AddWithValue("operatorName", decision.OperatorName.Trim());
            command.Parameters.AddWithValue("hospitalCode", hospitalCode);
            command.Parameters.AddWithValue("packageId", packageId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                throw new InvalidOperationException("该包尚无结构校验记录，请先执行一次校验。");
        }
        await using (var command = new NpgsqlCommand("""
            UPDATE lhyy.followup_package_import_state SET
                import_status = 'WaitingForDecision',
                requires_schema_review = true,
                updated_at = now()
            WHERE hospital_code = @hospitalCode AND package_id = @packageId
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("hospitalCode", hospitalCode);
            command.Parameters.AddWithValue("packageId", packageId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkAsync(string hospitalCode, string packageId, string status, string? errorCode, string? errorMessage, object? summary = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE lhyy.followup_package_import_state SET
                import_status = @status, error_code = @errorCode, error_message = @errorMessage,
                import_summary_json = CASE WHEN @summary IS NULL THEN import_summary_json ELSE @summary::jsonb END,
                staging_path = CASE WHEN @status IN ('BackingUp','Importing') THEN staging_path ELSE NULL END,
                started_at = CASE WHEN @status IN ('Validating','BackingUp','Importing','Restoring') THEN COALESCE(started_at, now()) ELSE started_at END,
                finished_at = CASE
                    WHEN @status IN ('Validating','BackingUp','Importing','Restoring') THEN NULL
                    WHEN @status IN ('Imported','ImportFailed','RejectedSchemaMismatch','Restored','RestoreFailed') THEN now()
                    ELSE finished_at
                END,
                updated_at = now()
            WHERE hospital_code = @hospitalCode AND package_id = @packageId
            """, connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.Add(new NpgsqlParameter("errorCode", NpgsqlDbType.Text) { Value = DbValue(errorCode) });
        command.Parameters.Add(new NpgsqlParameter("errorMessage", NpgsqlDbType.Text) { Value = DbValue(Truncate(errorMessage, 1000)) });
        command.Parameters.Add(new NpgsqlParameter("summary", NpgsqlDbType.Text) { Value = summary is null ? DBNull.Value : JsonSerializer.Serialize(summary, FollowUpJson.Options) });
        command.Parameters.AddWithValue("hospitalCode", hospitalCode);
        command.Parameters.AddWithValue("packageId", packageId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        operationCoordinator.InvalidatePersistentStateGate();
    }

    public async Task<Guid> AddBackupAsync(string hospitalCode, string packageId, FollowUpBackupArtifact artifact, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO lhyy.followup_package_backup_record
                (id, hospital_code, package_id, backup_type, backup_status, database_backup_path,
                 attachment_backup_path, backup_hash, backup_size_bytes, detail_json, finished_at)
            VALUES (@id, @hospitalCode, @packageId, 'DatabaseAndAttachments', 'Completed', @databasePath,
                    @attachmentPath, @hash, @size, @detail::jsonb, now())
            RETURNING id
            """, connection);
        command.Parameters.AddWithValue("id", artifact.RecordId);
        command.Parameters.AddWithValue("hospitalCode", hospitalCode);
        command.Parameters.AddWithValue("packageId", packageId);
        command.Parameters.AddWithValue("databasePath", artifact.DatabaseBackupPath);
        command.Parameters.AddWithValue("attachmentPath", artifact.AttachmentBackupPath);
        command.Parameters.AddWithValue("hash", artifact.Hash);
        command.Parameters.AddWithValue("size", artifact.SizeBytes);
        command.Parameters.AddWithValue("detail", JsonSerializer.Serialize(new { artifact.RootPath }, FollowUpJson.Options));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<FollowUpBackupArtifact?> GetLatestBackupAsync(string hospitalCode, string packageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, database_backup_path, attachment_backup_path, backup_hash, backup_size_bytes, detail_json->>'rootPath'
            FROM lhyy.followup_package_backup_record
            WHERE hospital_code = @hospitalCode AND package_id = @packageId AND backup_status = 'Completed'
            ORDER BY created_at DESC LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("hospitalCode", hospitalCode);
        command.Parameters.AddWithValue("packageId", packageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new FollowUpBackupArtifact(reader.GetGuid(0), ReadString(reader, 5) ?? Path.GetDirectoryName(reader.GetString(1))!, reader.GetString(1), reader.GetString(2), ReadString(reader, 3) ?? "", reader.GetInt64(4));
    }

    public async Task<Guid> StartRestoreAsync(
        FollowUpPackageImportState state,
        FollowUpBackupArtifact backup,
        string requestedBy,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var interruptedCommand = new NpgsqlCommand("""
            UPDATE lhyy.followup_package_restore_record SET
                restore_status = 'Failed',
                error_code = @interruptedErrorCode,
                error_message = @interruptedErrorMessage,
                finished_at = now()
            WHERE hospital_code = @hospitalCode AND package_id = @packageId
              AND restore_status = 'Running'
            """, connection, transaction))
        {
            interruptedCommand.Parameters.AddWithValue("hospitalCode", state.HospitalCode);
            interruptedCommand.Parameters.AddWithValue("packageId", state.PackageId);
            interruptedCommand.Parameters.AddWithValue("interruptedErrorCode", FollowUpErrorCodes.InternalError);
            interruptedCommand.Parameters.AddWithValue(
                "interruptedErrorMessage", "恢复进程中断，已由后续恢复批次接管。");
            await interruptedCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = new NpgsqlCommand("""
            INSERT INTO lhyy.followup_package_restore_record
                (id, hospital_code, package_id, restore_mode, restore_status, backup_record_id,
                 restore_plan_json, requested_by, started_at)
            VALUES
                (@id, @hospitalCode, @packageId, 'ReverseHead', 'Running', @backupRecordId,
                 @plan::jsonb, @requestedBy, now())
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("hospitalCode", state.HospitalCode);
        command.Parameters.AddWithValue("packageId", state.PackageId);
        command.Parameters.AddWithValue("backupRecordId", backup.RecordId);
        command.Parameters.AddWithValue("plan", JsonSerializer.Serialize(new
        {
            state.SequenceNo,
            state.PreviousPackageId,
            backup.DatabaseBackupPath,
            backup.AttachmentBackupPath
        }, FollowUpJson.Options));
        command.Parameters.AddWithValue("requestedBy", requestedBy);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task FinishRestoreAsync(
        Guid restoreId,
        string status,
        object? result,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE lhyy.followup_package_restore_record SET
                restore_status = @status,
                result_json = @result::jsonb,
                error_code = @errorCode,
                error_message = @errorMessage,
                finished_at = now()
            WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("id", restoreId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("result", JsonSerializer.Serialize(result ?? new { }, FollowUpJson.Options));
        command.Parameters.Add(new NpgsqlParameter("errorCode", NpgsqlDbType.Text) { Value = DbValue(errorCode) });
        command.Parameters.Add(new NpgsqlParameter("errorMessage", NpgsqlDbType.Text) { Value = DbValue(Truncate(errorMessage, 1000)) });
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
            throw new InvalidOperationException($"恢复审计记录不存在或不唯一：{restoreId}");
    }

    internal static bool ShouldUpdateRestoredState(
        Guid markerRestoreId,
        Guid currentRestoreId,
        string? importStatus) =>
        markerRestoreId == currentRestoreId && importStatus is "Restoring" or "Restored";

    internal static bool ShouldUpdateRestoreFailedState(
        Guid markerRestoreId,
        Guid currentRestoreId,
        string? importStatus) =>
        markerRestoreId == currentRestoreId && importStatus == "Restoring";

    internal static FollowUpRestoreReconciliationResult ResolvePendingReconciliation(
        Guid markerRestoreId,
        Guid currentRestoreId,
        string restoreStatus,
        bool confirmedFailure = false) => (restoreStatus, confirmedFailure) switch
        {
            ("Running", true) => FollowUpRestoreReconciliationResult.FailedFromMarker,
            ("Failed", true) => FollowUpRestoreReconciliationResult.AlreadyTerminal,
            (_, true) => FollowUpRestoreReconciliationResult.Conflict,
            ("Running", false) when markerRestoreId == currentRestoreId => FollowUpRestoreReconciliationResult.PendingCurrent,
            ("Running", false) => FollowUpRestoreReconciliationResult.SupersededInterrupted,
            ("Completed", false) => FollowUpRestoreReconciliationResult.CompletedFromAudit,
            ("Failed", false) => FollowUpRestoreReconciliationResult.AlreadyTerminal,
            _ => FollowUpRestoreReconciliationResult.Conflict
        };

    async Task<FollowUpRestoreReconciliationResult> IFollowUpRestoreCompletionReconciler.ReconcileAsync(
        FollowUpRestoreCompletionMarker marker,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        string? importStatus;
        await using (var stateCommand = new NpgsqlCommand("""
            SELECT import_status
            FROM lhyy.followup_package_import_state
            WHERE hospital_code = @hospitalCode AND package_id = @packageId
            FOR UPDATE
            """, connection, transaction))
        {
            stateCommand.Parameters.AddWithValue("hospitalCode", marker.HospitalCode);
            stateCommand.Parameters.AddWithValue("packageId", marker.PackageId);
            importStatus = await stateCommand.ExecuteScalarAsync(cancellationToken) as string;
        }
        if (importStatus is null) return FollowUpRestoreReconciliationResult.Conflict;

        string? restoreStatus = null;
        Guid? backupRecordId = null;
        await using (var restoreCommand = new NpgsqlCommand("""
            SELECT restore_status, backup_record_id
            FROM lhyy.followup_package_restore_record
            WHERE id = @restoreId AND hospital_code = @hospitalCode AND package_id = @packageId
            FOR UPDATE
            """, connection, transaction))
        {
            restoreCommand.Parameters.AddWithValue("restoreId", marker.RestoreId);
            restoreCommand.Parameters.AddWithValue("hospitalCode", marker.HospitalCode);
            restoreCommand.Parameters.AddWithValue("packageId", marker.PackageId);
            await using var reader = await restoreCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                restoreStatus = reader.GetString(0);
                backupRecordId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            }
        }
        if (restoreStatus is null || backupRecordId != marker.BackupRecordId)
            return FollowUpRestoreReconciliationResult.Conflict;

        Guid currentRestoreId;
        await using (var currentCommand = new NpgsqlCommand("""
            SELECT id
            FROM lhyy.followup_package_restore_record
            WHERE hospital_code = @hospitalCode AND package_id = @packageId
            ORDER BY requested_at DESC, id DESC
            LIMIT 1
            """, connection, transaction))
        {
            currentCommand.Parameters.AddWithValue("hospitalCode", marker.HospitalCode);
            currentCommand.Parameters.AddWithValue("packageId", marker.PackageId);
            currentRestoreId = (Guid)(await currentCommand.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("未找到当前恢复记录。"));
        }

        var stateUpdated = false;
        if (marker.RestoredAt is null)
        {
            var pendingResult = ResolvePendingReconciliation(
                marker.RestoreId,
                currentRestoreId,
                restoreStatus,
                marker.RestoreError is not null);
            if (pendingResult == FollowUpRestoreReconciliationResult.PendingCurrent
                || pendingResult == FollowUpRestoreReconciliationResult.Conflict)
                return pendingResult;

            if (pendingResult == FollowUpRestoreReconciliationResult.SupersededInterrupted)
            {
                await using var interruptedCommand = new NpgsqlCommand("""
                    UPDATE lhyy.followup_package_restore_record SET
                        restore_status = 'Failed',
                        error_code = @errorCode,
                        error_message = '恢复进程中断，已由后续恢复批次接管。',
                        finished_at = now()
                    WHERE id = @restoreId
                      AND hospital_code = @hospitalCode
                      AND package_id = @packageId
                      AND backup_record_id = @backupRecordId
                      AND restore_status = 'Running'
                    """, connection, transaction);
                interruptedCommand.Parameters.AddWithValue("restoreId", marker.RestoreId);
                interruptedCommand.Parameters.AddWithValue("hospitalCode", marker.HospitalCode);
                interruptedCommand.Parameters.AddWithValue("packageId", marker.PackageId);
                interruptedCommand.Parameters.AddWithValue("backupRecordId", marker.BackupRecordId);
                interruptedCommand.Parameters.AddWithValue("errorCode", FollowUpErrorCodes.InternalError);
                if (await interruptedCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("中断恢复审计记录补写期间发生并发变化。");
                await transaction.CommitAsync(cancellationToken);
                return pendingResult;
            }

            if (pendingResult == FollowUpRestoreReconciliationResult.FailedFromMarker)
            {
                await using var failedAuditCommand = new NpgsqlCommand("""
                    UPDATE lhyy.followup_package_restore_record SET
                        restore_status = 'Failed',
                        error_code = @errorCode,
                        error_message = @errorMessage,
                        finished_at = now()
                    WHERE id = @restoreId
                      AND hospital_code = @hospitalCode
                      AND package_id = @packageId
                      AND backup_record_id = @backupRecordId
                      AND restore_status = 'Running'
                    """, connection, transaction);
                failedAuditCommand.Parameters.AddWithValue("restoreId", marker.RestoreId);
                failedAuditCommand.Parameters.AddWithValue("hospitalCode", marker.HospitalCode);
                failedAuditCommand.Parameters.AddWithValue("packageId", marker.PackageId);
                failedAuditCommand.Parameters.AddWithValue("backupRecordId", marker.BackupRecordId);
                failedAuditCommand.Parameters.AddWithValue("errorCode", FollowUpErrorCodes.InternalError);
                failedAuditCommand.Parameters.AddWithValue("errorMessage", Truncate(marker.RestoreError, 1000)!);
                if (await failedAuditCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("恢复失败审计记录补写期间发生并发变化。");
            }

            if (pendingResult is FollowUpRestoreReconciliationResult.FailedFromMarker
                or FollowUpRestoreReconciliationResult.AlreadyTerminal)
            {
                if (ShouldUpdateRestoreFailedState(marker.RestoreId, currentRestoreId, importStatus))
                {
                    await using var failedStateCommand = new NpgsqlCommand("""
                        UPDATE lhyy.followup_package_import_state SET
                            import_status = 'RestoreFailed',
                            error_code = @errorCode,
                            error_message = '恢复失败状态由后台补写。',
                            staging_path = NULL,
                            finished_at = now(),
                            updated_at = now()
                        WHERE hospital_code = @hospitalCode
                          AND package_id = @packageId
                          AND import_status = 'Restoring'
                        """, connection, transaction);
                    failedStateCommand.Parameters.AddWithValue("hospitalCode", marker.HospitalCode);
                    failedStateCommand.Parameters.AddWithValue("packageId", marker.PackageId);
                    failedStateCommand.Parameters.AddWithValue("errorCode", FollowUpErrorCodes.InternalError);
                    if (await failedStateCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
                        throw new InvalidOperationException("恢复失败状态补写期间发生并发变化。");
                    stateUpdated = true;
                }
                await transaction.CommitAsync(cancellationToken);
                if (stateUpdated) operationCoordinator.InvalidatePersistentStateGate();
                return pendingResult;
            }

            marker = marker with { RestoredAt = DateTimeOffset.Now };
        }

        if (restoreStatus is not ("Running" or "Completed"))
            return FollowUpRestoreReconciliationResult.Conflict;
        var auditCompleted = restoreStatus == "Completed";

        if (!auditCompleted)
        {
            await using var auditCommand = new NpgsqlCommand("""
                UPDATE lhyy.followup_package_restore_record SET
                    restore_status = 'Completed',
                    result_json = @result::jsonb,
                    error_code = NULL,
                    error_message = NULL,
                    finished_at = now()
                WHERE id = @restoreId
                  AND hospital_code = @hospitalCode
                  AND package_id = @packageId
                  AND backup_record_id = @backupRecordId
                  AND restore_status = 'Running'
                """, connection, transaction);
            auditCommand.Parameters.AddWithValue("restoreId", marker.RestoreId);
            auditCommand.Parameters.AddWithValue("hospitalCode", marker.HospitalCode);
            auditCommand.Parameters.AddWithValue("packageId", marker.PackageId);
            auditCommand.Parameters.AddWithValue("backupRecordId", marker.BackupRecordId);
            auditCommand.Parameters.AddWithValue("result", JsonSerializer.Serialize(new
            {
                marker.BackupRecordId,
                marker.RestoredAt,
                marker.AuditError
            }, FollowUpJson.Options));
            if (await auditCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("恢复审计记录补写期间发生并发变化。");
        }

        stateUpdated = ShouldUpdateRestoredState(marker.RestoreId, currentRestoreId, importStatus);
        if (stateUpdated)
        {
            await using var stateCommand = new NpgsqlCommand("""
                UPDATE lhyy.followup_package_import_state SET
                    import_status = 'Restored',
                    error_code = NULL,
                    error_message = '数据库和附件已恢复，管理状态由后台补写。',
                    import_summary_json = @summary::jsonb,
                    staging_path = NULL,
                    finished_at = now(),
                    updated_at = now()
                WHERE hospital_code = @hospitalCode
                  AND package_id = @packageId
                  AND import_status IN ('Restoring', 'Restored')
                """, connection, transaction);
            stateCommand.Parameters.AddWithValue("hospitalCode", marker.HospitalCode);
            stateCommand.Parameters.AddWithValue("packageId", marker.PackageId);
            stateCommand.Parameters.AddWithValue("summary", JsonSerializer.Serialize(new
            {
                marker.BackupRecordId,
                marker.RestoredAt,
                marker.AuditError
            }, FollowUpJson.Options));
            if (await stateCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("恢复状态补写期间发生并发变化。");
        }

        await AddRestoreCompletionLogCoreAsync(connection, transaction, marker, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        if (stateUpdated) operationCoordinator.InvalidatePersistentStateGate();
        return stateUpdated
            ? FollowUpRestoreReconciliationResult.CompletedCurrent
            : auditCompleted
                ? FollowUpRestoreReconciliationResult.AlreadyCompleted
                : FollowUpRestoreReconciliationResult.CompletedAuditOnly;
    }

    internal async Task AddRestoreCompletionLogAsync(
        FollowUpRestoreCompletionMarker marker,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("""
            SELECT id FROM lhyy.followup_package_restore_record
            WHERE id = @restoreId
              AND hospital_code = @hospitalCode
              AND package_id = @packageId
              AND backup_record_id = @backupRecordId
            FOR UPDATE
            """, connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("restoreId", marker.RestoreId);
            lockCommand.Parameters.AddWithValue("hospitalCode", marker.HospitalCode);
            lockCommand.Parameters.AddWithValue("packageId", marker.PackageId);
            lockCommand.Parameters.AddWithValue("backupRecordId", marker.BackupRecordId);
            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is null)
                throw new InvalidOperationException("恢复审计记录与完成标记不匹配。");
        }
        await AddRestoreCompletionLogCoreAsync(connection, transaction, marker, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task AddRestoreCompletionLogCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        FollowUpRestoreCompletionMarker marker,
        CancellationToken cancellationToken)
    {
        await using var logCommand = new NpgsqlCommand("""
            INSERT INTO lhyy.followup_package_import_log
                (hospital_code, package_id, operation, level, message, detail_json)
            SELECT @hospitalCode, @packageId, 'restore', 'Info',
                   '数据库和附件已从导入前备份恢复', @detail::jsonb
            WHERE NOT EXISTS (
                SELECT 1
                FROM lhyy.followup_package_import_log
                WHERE hospital_code = @hospitalCode
                  AND package_id = @packageId
                  AND operation = 'restore'
                  AND detail_json->>'restoreId' = @restoreId)
            """, connection, transaction);
        logCommand.Parameters.AddWithValue("hospitalCode", marker.HospitalCode);
        logCommand.Parameters.AddWithValue("packageId", marker.PackageId);
        logCommand.Parameters.AddWithValue("restoreId", marker.RestoreId.ToString());
        logCommand.Parameters.AddWithValue("detail", JsonSerializer.Serialize(new
        {
            restoreId = marker.RestoreId,
            marker.BackupRecordId,
            marker.RestoredAt,
            marker.AuditError
        }, FollowUpJson.Options));
        await logCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnqueueAckAsync(FollowUpPackageAck ack, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid id;
        await using (var command = new NpgsqlCommand("""
            INSERT INTO cyyy.followup_package_ack_queue
                (hospital_code, package_id, ack_status, ack_payload_json, forward_status, updated_at)
            VALUES (@hospitalCode, @packageId, @status, '{}'::jsonb, 'Pending', now())
            ON CONFLICT (hospital_code, package_id, ack_status) DO UPDATE SET
                forward_status = CASE WHEN cyyy.followup_package_ack_queue.forward_status = 'Forwarded' THEN 'Forwarded' ELSE 'Pending' END,
                updated_at = now()
            RETURNING id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("hospitalCode", ack.HospitalCode);
            command.Parameters.AddWithValue("packageId", ack.PackageId);
            command.Parameters.AddWithValue("status", ack.AckStatus);
            id = (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
        }
        ack.AckId = id.ToString();
        await using (var command = new NpgsqlCommand("""
            UPDATE cyyy.followup_package_ack_queue SET ack_payload_json = @payload::jsonb WHERE id = @id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(ack, FollowUpJson.Options));
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AddLogAsync(string hospitalCode, string packageId, string operation, string level, string message, object? detail, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO lhyy.followup_package_import_log
                (hospital_code, package_id, operation, level, message, detail_json)
            VALUES (@hospitalCode, @packageId, @operation, @level, @message, @detail::jsonb)
            """, connection);
        command.Parameters.AddWithValue("hospitalCode", hospitalCode);
        command.Parameters.AddWithValue("packageId", packageId);
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

    private static FollowUpPackageImportState ReadState(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0), HospitalCode = reader.GetString(1), PackageId = reader.GetString(2), SequenceNo = reader.GetInt64(3),
        PackageType = reader.GetString(4), ImportStatus = reader.GetString(5), PreviousPackageId = ReadString(reader, 6),
        PackageHash = ReadString(reader, 7) ?? "", LocalPackagePath = reader.GetString(8), StagingPath = ReadString(reader, 9),
        SchemaDiffLevel = ReadString(reader, 10), RequiresSchemaReview = reader.GetBoolean(11), ErrorCode = ReadString(reader, 12),
        ErrorMessage = ReadString(reader, 13), StartedAt = ReadDateTime(reader, 14), FinishedAt = ReadDateTime(reader, 15)
    };
    private static object DbValue(object? value) => value ?? DBNull.Value;
    private static string? ReadString(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTime? ReadDateTime(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    private static string? Truncate(string? value, int max) => value is { Length: > 0 } ? value[..Math.Min(value.Length, max)] : value;
}
