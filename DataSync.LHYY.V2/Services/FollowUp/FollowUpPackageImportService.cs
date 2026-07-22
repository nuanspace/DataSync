using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json;

namespace DataSync.LHYY.V2.Services.FollowUp;

public sealed class FollowUpPackageImportService(
    IConfiguration configuration,
    IOptions<FollowUpPackageImportOptions> options,
    FollowUpPackageVerifyService verifyService,
    FollowUpPackageSchemaCheckService schemaCheckService,
    FollowUpTargetAdaptationService targetAdaptationService,
    FollowUpEdcScopeService edcScopeService,
    FollowUpPackageBackupService backupService,
    FollowUpPackageImportRepository repository,
    FollowUpCubeOperationCoordinator operationCoordinator,
    ILogger<FollowUpPackageImportService> logger)
{
    internal const string NTCareRestartRequiredCode = "NTCARE_RESTART_REQUIRED";
    internal const string NTCareRestartRequiredMessage = "数据已成功导入；NTCare 未提供缓存刷新接口，请重启 NTCare 或执行既有运维缓存刷新流程后查看最新表单。";

    private readonly string _cubeConnectionString = configuration.GetConnectionString("CubeDb")
        ?? throw new InvalidOperationException("未找到连接字符串 'CubeDb'");
    private readonly FollowUpPackageImportOptions _options = options.Value;

    public async Task<FollowUpImportOperationResult> ImportAsync(
        FollowUpPackageImportState state,
        bool allowBaseline,
        CancellationToken cancellationToken = default)
    {
        if (!FollowUpDisplayText.CanImport(state.ImportStatus))
            return new FollowUpImportOperationResult(false, $"当前状态不允许导入：{state.ImportStatus}。");
        if (await repository.HasUnsafeOperationAsync(cancellationToken))
            return new FollowUpImportOperationResult(false, "检测到导入或恢复中断状态，请先完成对应包的备份恢复。");
        await using var operationLease = await operationCoordinator.TryAcquireExclusiveAsync(cancellationToken);
        if (operationLease is null)
            return new FollowUpImportOperationResult(false, "CubeDb 当前有写入或维护任务正在执行。");
        var authoritativeStatus = await repository.GetPackageStatusAsync(
            state.HospitalCode, state.PackageId, cancellationToken);
        if (authoritativeStatus.Imported)
            return new FollowUpImportOperationResult(true, "该包已成功导入，无需重复执行。");
        var hasUnsafeOperation = await repository.HasUnsafeOperationAsync(cancellationToken);
        if (!FollowUpDisplayText.CanStartImport(authoritativeStatus.Status, hasUnsafeOperation))
        {
            if (hasUnsafeOperation)
                return new FollowUpImportOperationResult(false, "检测到导入或恢复中断状态，请先完成对应包的备份恢复。");
            return new FollowUpImportOperationResult(false, $"数据库中的当前状态不允许导入：{authoritativeStatus.Status ?? "不存在"}。");
        }
        var startedAt = DateTimeOffset.Now;
        FollowUpVerifiedPackage? package = null;
        FollowUpBackupArtifact? backup = null;
        var importCommitted = false;
        var attachmentRestoreFailed = false;
        try
        {
            await repository.MarkAsync(state.HospitalCode, state.PackageId, "Validating", null, null, cancellationToken: cancellationToken);
            package = await verifyService.VerifyAndExtractAsync(
                state.LocalPackagePath,
                state.PackageHash,
                state.HospitalCode,
                state.PackageId,
                state.SequenceNo,
                state.PackageType,
                cancellationToken);
            ValidateVersions(package.Manifest);

            if (package.Manifest.PackageType == "Baseline" && !allowBaseline)
            {
                await repository.MarkAsync(state.HospitalCode, state.PackageId, "WaitingForDecision", null, "Baseline 必须在页面确认后导入。", cancellationToken: cancellationToken);
                return new FollowUpImportOperationResult(false, "Baseline 必须在页面确认后导入。");
            }
            var currentHead = await repository.GetCurrentMainHeadAsync(state.HospitalCode, cancellationToken);
            var related = await repository.GetPackageStatusAsync(state.HospitalCode, package.Manifest.RelatedPackageId, cancellationToken);
            var chain = FollowUpPackageChain.Evaluate(new FollowUpPackageChainRequest(
                package.Manifest.PackageType,
                package.Manifest.PreviousPackageId,
                package.Manifest.RelatedPackageId,
                package.Manifest.SequenceNo,
                currentHead,
                related.Exists,
                related.Imported));
            if (!chain.CanImport)
            {
                var waiting = chain.ErrorCode == FollowUpErrorCodes.PackageNotAvailable && !related.Imported;
                await repository.MarkAsync(
                    state.HospitalCode, state.PackageId,
                    waiting ? "WaitingForPredecessor" : "ImportFailed",
                    chain.ErrorCode, chain.Message, cancellationToken: cancellationToken);
                return new FollowUpImportOperationResult(false, chain.Message ?? "包链校验失败。", chain.ErrorCode);
            }

            var schemaDecision = await repository.GetSchemaDecisionAsync(state.HospitalCode, state.PackageId, cancellationToken);
            var approvedDecision = schemaDecision?.DecisionStatus == "ApprovedMapping" ? schemaDecision : null;
            var schemaCheck = await schemaCheckService.CheckAsync(package.SchemaSnapshot, package.TableManifest, approvedDecision, cancellationToken);
            await repository.SaveVerifiedAsync(package, schemaCheck, cancellationToken);
            if (!schemaCheck.Compatible)
            {
                var status = schemaCheck.DiffLevel == "Breaking" ? "RejectedSchemaMismatch" : "WaitingForDecision";
                await repository.MarkAsync(state.HospitalCode, state.PackageId, status, FollowUpErrorCodes.SchemaReviewRequired,
                    string.Join("；", schemaCheck.Messages.Take(10)), cancellationToken: cancellationToken);
                if (status == "RejectedSchemaMismatch")
                    await EnqueueAckAsync(package, "RejectedSchemaMismatch", startedAt, FollowUpErrorCodes.SchemaReviewRequired, "目标数据库结构不兼容。", cancellationToken);
                return new FollowUpImportOperationResult(false, $"结构校验结果：{schemaCheck.DiffLevel}", FollowUpErrorCodes.SchemaReviewRequired);
            }

            FollowUpEdcScopePlan edcScopePlan;
            try
            {
                await targetAdaptationService.EnsureReadyAsync(cancellationToken);
                edcScopePlan = await edcScopeService.PrepareAsync(package, approvedDecision, cancellationToken);
            }
            catch (FollowUpPackageException ex) when (ex.ErrorCode == FollowUpErrorCodes.SchemaReviewRequired)
            {
                await repository.MarkAsync(
                    state.HospitalCode,
                    state.PackageId,
                    "WaitingForDecision",
                    ex.ErrorCode,
                    ex.Message,
                    cancellationToken: cancellationToken);
                return new FollowUpImportOperationResult(false, ex.Message, ex.ErrorCode);
            }

            await repository.MarkAsync(state.HospitalCode, state.PackageId, "BackingUp", null, null, cancellationToken: cancellationToken);
            backup = await backupService.CreateAsync(package, cancellationToken);
            await repository.AddBackupAsync(state.HospitalCode, state.PackageId, backup, cancellationToken);

            await repository.MarkAsync(state.HospitalCode, state.PackageId, "Importing", null, null, cancellationToken: cancellationToken);
            return await ExecuteCommitBoundaryAsync(
                async () =>
                {
                    var counts = await ImportDataAsync(
                        package,
                        approvedDecision,
                        edcScopePlan,
                        () => backupService.InstallAttachmentsAsync(package, cancellationToken),
                        () => importCommitted = true,
                        cancellationToken);
                    return counts;
                },
                async importException =>
                {
                    if (importCommitted)
                        return;

                    try
                    {
                        await backupService.RestoreAttachmentsAsync(backup.AttachmentBackupPath, CancellationToken.None);
                    }
                    catch (Exception restoreException)
                    {
                        attachmentRestoreFailed = true;
                        logger.LogCritical(restoreException,
                            "FollowUp 导入失败后的附件恢复失败，必须停止后续导入。PackageId={PackageId}", state.PackageId);
                        throw new InvalidOperationException(
                            "数据导入失败且附件恢复失败，当前数据状态不确定，必须人工处置。",
                            new AggregateException(importException, restoreException));
                    }
                },
                async counts =>
                {
                    await repository.MarkAsync(
                        state.HospitalCode,
                        state.PackageId,
                        "Imported",
                        NTCareRestartRequiredCode,
                        NTCareRestartRequiredMessage,
                        new { tables = counts.Count, records = counts.Values.Sum(), counts, ntcareAction = "restart-required" },
                        cancellationToken);
                    await EnqueueAckAsync(
                        package,
                        "Imported",
                        startedAt,
                        NTCareRestartRequiredCode,
                        NTCareRestartRequiredMessage,
                        cancellationToken);
                    await repository.AddLogAsync(
                        state.HospitalCode,
                        state.PackageId,
                        "import",
                        "Warning",
                        "FollowUp 数据包导入完成，NTCare 需要重启或执行既有缓存刷新流程",
                        new
                        {
                            records = counts.Values.Sum(),
                            tables = counts.Count,
                            code = NTCareRestartRequiredCode,
                            ntcareAction = "restart-required"
                        },
                        cancellationToken);
                    return new FollowUpImportOperationResult(
                        true,
                        $"数据导入完成，共 {counts.Values.Sum()} 条记录；请重启 NTCare 或执行既有缓存刷新流程。",
                        NTCareRestartRequiredCode);
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FollowUp 包导入失败。HospitalCode={HospitalCode}, PackageId={PackageId}", state.HospitalCode, state.PackageId);
            if (importCommitted)
            {
                logger.LogCritical(ex, "FollowUp 数据和附件已提交，但写入导入成功状态或 ACK 失败，禁止降级为失败。PackageId={PackageId}", state.PackageId);
                try
                {
                    await repository.MarkAsync(state.HospitalCode, state.PackageId, "Imported", NTCareRestartRequiredCode,
                        $"{NTCareRestartRequiredMessage} 成功状态或 ACK 首次写入失败，请检查日志并重试状态同步。",
                        new { committed = true, statusWriteError = ex.Message, ntcareAction = "restart-required" }, CancellationToken.None);
                    if (package is not null)
                        await EnqueueAckAsync(
                            package,
                            "Imported",
                            startedAt,
                            NTCareRestartRequiredCode,
                            NTCareRestartRequiredMessage,
                            CancellationToken.None);
                }
                catch (Exception retryException)
                {
                    logger.LogCritical(retryException, "FollowUp 已提交数据的成功状态补写失败。PackageId={PackageId}", state.PackageId);
                }
                return new FollowUpImportOperationResult(true, "数据和附件已提交；成功状态或 ACK 写入异常，请检查 DataSync 日志。", FollowUpErrorCodes.InternalError);
            }
            try
            {
                var failureStatus = ResolveFailureStatus(attachmentRestoreFailed);
                await repository.MarkAsync(state.HospitalCode, state.PackageId, failureStatus, ErrorCode(ex), ex.Message, cancellationToken: CancellationToken.None);
                if (package is not null)
                    await EnqueueAckAsync(package, "ImportFailed", startedAt, ErrorCode(ex), ex.Message, CancellationToken.None);
                await repository.AddLogAsync(state.HospitalCode, state.PackageId, "import", "Error", "FollowUp 数据包导入失败",
                    new { errorCode = ErrorCode(ex), failureStatus }, CancellationToken.None);
            }
            catch (Exception recordException)
            {
                logger.LogError(recordException, "记录 FollowUp 导入失败状态时再次失败。");
            }
            return new FollowUpImportOperationResult(false, ex.Message, ErrorCode(ex));
        }
        finally
        {
            if (package is not null) CleanupStaging(package.StagingPath, logger);
        }
    }

    private void ValidateVersions(FollowUpPackageManifest manifest)
    {
        if (!string.Equals(manifest.ExportContractVersion, _options.SupportedContractVersion, StringComparison.Ordinal))
            throw new FollowUpPackageException(FollowUpErrorCodes.ContractVersionUnsupported, "导出契约版本不受支持。");
        if (!Version.TryParse(_options.ImporterVersion, out var importer)
            || !Version.TryParse(manifest.MinImporterVersion, out var minimum)
            || importer < minimum)
            throw new FollowUpPackageException(FollowUpErrorCodes.ContractVersionUnsupported, "导入器版本低于数据包要求。");
    }

    private async Task<Dictionary<string, int>> ImportDataAsync(
        FollowUpVerifiedPackage package,
        FollowUpSchemaDecision? schemaDecision,
        FollowUpEdcScopePlan edcScopePlan,
        Func<Task> beforeCommit,
        Action onCommitted,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var connection = new NpgsqlConnection(_cubeConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var followUpPatients = new Dictionary<Guid, FollowUpPatientSource>();
            var ordered = package.TableManifest
                .Where(item => item.Enabled && !item.Skipped && !string.IsNullOrWhiteSpace(item.ExportPath))
                .OrderBy(item => CategoryOrder(item.DataCategory))
                .ThenBy(item => package.TableManifest.IndexOf(item));
            foreach (var table in ordered)
            {
                var targetTable = FollowUpSchemaDecisionProcessor.MapManifest(table, schemaDecision);
                FollowUpPackageSchemaCheckService.EnsureIdentifier(targetTable.Schema);
                FollowUpPackageSchemaCheckService.EnsureIdentifier(targetTable.TableName);
                var originalSourceSchema = package.SchemaSnapshot.Tables.First(item =>
                    item.SchemaName.Equals(table.Schema, StringComparison.OrdinalIgnoreCase)
                    && item.TableName.Equals(table.TableName, StringComparison.OrdinalIgnoreCase));
                var sourceSchema = FollowUpSchemaDecisionProcessor.MapSchema(originalSourceSchema, schemaDecision);
                var writableColumns = await GetWritableColumnsAsync(connection, transaction, targetTable.Schema, targetTable.TableName, cancellationToken);
                var columns = sourceSchema.Columns.Select(item => item.Name)
                    .Where(column => writableColumns.Contains(column))
                    .ToList();
                var mapping = FollowUpSchemaDecisionProcessor.FindMapping(table.Schema, table.TableName, schemaDecision);
                foreach (var defaultColumn in mapping?.DefaultValues.Keys ?? Enumerable.Empty<string>())
                    if (writableColumns.Contains(defaultColumn) && !columns.Contains(defaultColumn, StringComparer.OrdinalIgnoreCase))
                        columns.Add(defaultColumn);
                var primaryKey = targetTable.PrimaryKey.Count > 0 ? targetTable.PrimaryKey : sourceSchema.PrimaryKey;
                if (primaryKey.Count == 0 || primaryKey.Any(column => !columns.Contains(column, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"表 {targetTable.Schema}.{targetTable.TableName} 缺少可用主键，不能幂等导入。");

                var filePath = SafeStagingPath(package.StagingPath, table.ExportPath!);
                var count = 0;
                using var reader = new StreamReader(filePath);
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var _ = JsonDocument.Parse(line);
                    var mappedLine = FollowUpSchemaDecisionProcessor.MapRow(line, table.Schema, table.TableName, schemaDecision);
                    if (FollowUpTargetAdaptationService.ReadPatientSource(
                            targetTable.Schema,
                            targetTable.TableName,
                            mappedLine) is { } patientSource)
                        followUpPatients[patientSource.PatientId] = patientSource;
                    var adaptedLine = FollowUpTargetAdaptationService.AdaptRow(
                        targetTable.Schema,
                        targetTable.TableName,
                        mappedLine);
                    if (table.ImportPolicy is "UseExistingById" or "RejectIfMissing")
                    {
                        if (!await ExistsAsync(connection, transaction, targetTable.Schema, targetTable.TableName, primaryKey, adaptedLine, cancellationToken))
                            throw new InvalidOperationException($"{table.ImportPolicy} 要求的基础记录不存在：{targetTable.Schema}.{targetTable.TableName}");
                    }
                    else
                    {
                        var sql = FollowUpUpsertSqlBuilder.Build(targetTable.Schema, targetTable.TableName, columns, primaryKey, table.ImportPolicy);
                        await using var command = new NpgsqlCommand(sql, connection, transaction);
                        command.Parameters.AddWithValue("row", adaptedLine);
                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }
                    count++;
                }
                if (count != table.RecordCount)
                    throw new InvalidDataException($"表 {table.Schema}.{table.TableName} 记录数与清单不一致。");
                result[$"{targetTable.Schema}.{targetTable.TableName}"] = count;
            }
            var sourceMapCount = await targetAdaptationService.ApplySourceMapAsync(
                connection,
                transaction,
                followUpPatients.Values.ToArray(),
                package.Manifest.HospitalCode,
                package.Manifest.PackageId,
                cancellationToken);
            if (sourceMapCount > 0)
                result["datasync.followup_patient_source_map"] = sourceMapCount;
            var scopeMapCount = await edcScopeService.ApplyAsync(connection, transaction, edcScopePlan, cancellationToken);
            if (scopeMapCount > 0)
                result["public.patient_data_scope_map"] = scopeMapCount;
            // 附件先原子替换，随后提交数据库；任一环节失败都会回滚数据库并由上层恢复附件。
            await beforeCommit();
            await transaction.CommitAsync(cancellationToken);
            // 必须在 CommitAsync 成功的瞬间标记；之后即使事务/连接释放失败，也不能恢复旧附件。
            onCommitted();
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<HashSet<string>> GetWritableColumnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
              AND is_generated = 'NEVER'
              AND (is_identity = 'NO' OR identity_generation IS DISTINCT FROM 'ALWAYS')
            """, connection, transaction);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<bool> ExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        IReadOnlyCollection<string> primaryKey,
        string row,
        CancellationToken cancellationToken)
    {
        var quotedTable = $"\"{schema}\".\"{table}\"";
        var comparison = string.Join(" AND ", primaryKey.Select(column => $"target.\"{column}\" IS NOT DISTINCT FROM source.\"{column}\""));
        var sql = $"""
            SELECT EXISTS (
                SELECT 1 FROM {quotedTable} target,
                jsonb_populate_record(NULL::{quotedTable}, @row::jsonb) source
                WHERE {comparison})
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("row", row);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task EnqueueAckAsync(
        FollowUpVerifiedPackage package,
        string status,
        DateTimeOffset startedAt,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await repository.EnqueueAckAsync(new FollowUpPackageAck
        {
            HospitalCode = package.Manifest.HospitalCode,
            PackageId = package.Manifest.PackageId,
            DeviceId = _options.DeviceId,
            AckStatus = status,
            ImporterVersion = _options.ImporterVersion,
            ReceivedHash = package.PackageHash,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.Now,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage is { Length: > 1000 } ? errorMessage[..1000] : errorMessage,
            Detail = JsonSerializer.SerializeToElement(new
            {
                sequenceNo = package.Manifest.SequenceNo,
                packageType = package.Manifest.PackageType,
                targetAdaptation = new
                {
                    patientSourceType = "care",
                    sourceMarker = "datasync.followup_patient_source_map"
                },
                code = status == "Imported" ? NTCareRestartRequiredCode : null,
                ntcareAction = status == "Imported" ? "restart-required" : null
            }, FollowUpJson.Options)
        }, cancellationToken);
    }

    private static string SafeStagingPath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("数据文件路径逃逸 staging 目录。");
        return target;
    }
    internal static string ResolveFailureStatus(bool attachmentRestoreFailed) =>
        attachmentRestoreFailed ? "RestoreFailed" : "ImportFailed";

    internal static async Task<TResult> ExecuteCommitBoundaryAsync<TCommit, TResult>(
        Func<Task<TCommit>> commitAsync,
        Func<Exception, Task> recoverPreCommitFailureAsync,
        Func<TCommit, Task<TResult>> finalizeCommittedAsync)
    {
        TCommit committed;
        try
        {
            committed = await commitAsync();
        }
        catch (Exception ex)
        {
            await recoverPreCommitFailureAsync(ex);
            throw;
        }

        return await finalizeCommittedAsync(committed);
    }

    internal static void CleanupStaging(string stagingPath, ILogger logger)
    {
        try
        {
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "清理 FollowUp staging 目录失败。StagingPath={StagingPath}", stagingPath);
        }
    }

    private static int CategoryOrder(string category) => category switch { "ReferenceMaster" => 0, "Relationship" => 1, "BusinessData" => 2, _ => 3 };
    private static string ErrorCode(Exception exception) => exception is FollowUpPackageException package ? package.ErrorCode : FollowUpErrorCodes.InternalError;
}
