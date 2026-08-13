using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace DataSync.LHYY.V2.Services.FollowUp;

public sealed class FollowUpPackageImportService(
    IConfiguration configuration,
    IOptions<FollowUpPackageImportOptions> options,
    FollowUpPackageVerifyService verifyService,
    FollowUpPackageSchemaCheckService schemaCheckService,
    FollowUpPatientIdentityService patientIdentityService,
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
        var commitAttempted = false;
        var commitOutcomeUnknown = false;
        var installedAttachments = new List<FollowUpAttachmentMutation>();
        var attachmentRestoreFailed = false;
        FollowUpPatientIdentityMap[] patientMappings = [];
        Dictionary<string, int> committedCounts = [];
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
            ValidateVersions(package.Manifest, _options);
            ValidateHospitalIdentity(package.Manifest, _options);

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
            var importedFormQuestionContentHash = await repository.GetTableContentHashAsync(
                state.HospitalCode,
                currentHead,
                "form",
                "form_question",
                cancellationToken);
            var schemaCheck = await schemaCheckService.CheckAsync(
                package,
                importedFormQuestionContentHash,
                approvedDecision,
                cancellationToken);
            await repository.SaveVerifiedAsync(package, schemaCheck, cancellationToken);
            if (schemaCheck.IgnoredNonNullColumns is { Count: > 0 } ignoredColumns)
                logger.LogWarning(
                    "FollowUp 动态字段按医院表单项范围导入，已忽略非关联非空字段。PackageId={PackageId}, ColumnCount={ColumnCount}, NonNullRowCount={NonNullRowCount}",
                    state.PackageId,
                    ignoredColumns.Count,
                    ignoredColumns.Sum(item => item.NonNullRowCount));
            if (!schemaCheck.Compatible)
            {
                var status = schemaCheck.DiffLevel == "Breaking" ? "RejectedSchemaMismatch" : "WaitingForDecision";
                await repository.MarkAsync(state.HospitalCode, state.PackageId, status, FollowUpErrorCodes.SchemaReviewRequired,
                    FormatSchemaCheckSummary(schemaCheck.Messages), cancellationToken: cancellationToken);
                if (status == "RejectedSchemaMismatch")
                    await EnqueueAckAsync(package, "RejectedSchemaMismatch", startedAt, FollowUpErrorCodes.SchemaReviewRequired, "目标数据库结构不兼容。", cancellationToken);
                return new FollowUpImportOperationResult(false, $"结构校验结果：{schemaCheck.DiffLevel}", FollowUpErrorCodes.SchemaReviewRequired);
            }

            TargetQuestionScopeGuard? targetQuestionScopeGuard = null;
            var packageProjectScope = await FollowUpPackageSchemaCheckService.ReadPackageProjectScopeAsync(
                package,
                cancellationToken);
            PackageQuestionProjectGuard? packageQuestionProjectGuard =
                CreatePackageProjectGuard(packageProjectScope);
            var hasDynamicScopes = schemaCheck.TableColumnScopes is { Count: > 0 };
            var hasPackageQuestionPayload = package.TableManifest.Any(item =>
                item.Enabled
                && !item.Skipped
                && !string.IsNullOrWhiteSpace(item.ExportPath)
                && item.Schema.Equals("form", StringComparison.OrdinalIgnoreCase)
                && item.TableName.Equals("form_question", StringComparison.OrdinalIgnoreCase));
            if (hasDynamicScopes || hasPackageQuestionPayload)
            {
                var questionItem = package.TableManifest.Single(item =>
                    item.Schema.Equals("form", StringComparison.OrdinalIgnoreCase)
                    && item.TableName.Equals("form_question", StringComparison.OrdinalIgnoreCase));
                var questionScopeSource = FollowUpPackageSchemaCheckService.ResolveQuestionScopeSource(
                    package.Manifest.PackageType,
                    questionItem,
                    importedFormQuestionContentHash);
                if (questionScopeSource == FollowUpQuestionScopeSource.Package)
                {
                    var packageQuestionScope = await FollowUpPackageSchemaCheckService.ReadPackageQuestionScopeAsync(
                        package,
                        questionItem,
                        cancellationToken);
                    packageQuestionProjectGuard = CreatePackageQuestionProjectGuard(packageQuestionScope);
                }
                else if (hasDynamicScopes)
                {
                    var questionSchema = package.SchemaSnapshot.Tables.Single(item =>
                        item.SchemaName.Equals("form", StringComparison.OrdinalIgnoreCase)
                        && item.TableName.Equals("form_question", StringComparison.OrdinalIgnoreCase));
                    targetQuestionScopeGuard = CreateTargetQuestionScopeGuard(
                        questionScopeSource,
                        questionItem.ContentHash!,
                        questionSchema.Columns.Select(item => item.Name).ToList());
                }
            }

            FollowUpPatientIdentityScope patientIdentityScope;
            FollowUpPatientIdentityPlan patientIdentityPlan;
            FollowUpEdcScopePlan edcScopePlan;
            try
            {
                await patientIdentityService.EnsureReadyAsync(cancellationToken);
                patientIdentityScope = await patientIdentityService.ReadScopeAsync(
                    package,
                    approvedDecision,
                    cancellationToken);
                patientIdentityPlan = await patientIdentityService.PrepareAsync(
                    patientIdentityScope,
                    cancellationToken);
                patientMappings = patientIdentityPlan.Patients.Values.ToArray();
                edcScopePlan = await edcScopeService.PrepareAsync(
                    package,
                    approvedDecision,
                    patientIdentityPlan.PatientIdMap,
                    cancellationToken);
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
            var attachmentBackupSnapshot =
                await backupService.CreateValidatedAttachmentSnapshotAsync(backup, cancellationToken);
            Exception? attachmentSnapshotOperationException = null;
            try
            {
                await repository.MarkAsync(state.HospitalCode, state.PackageId, "Importing", null, null, cancellationToken: cancellationToken);
                return await ExecuteCommitBoundaryAsync(
                async () =>
                {
                    var counts = await ImportDataAsync(
                        package,
                        approvedDecision,
                        schemaCheck.TableColumnScopes ?? [],
                        targetQuestionScopeGuard,
                        packageQuestionProjectGuard,
                        patientIdentityScope,
                        patientIdentityPlan,
                        edcScopePlan,
                        async () =>
                        {
                            try
                            {
                                installedAttachments.AddRange(await backupService.InstallAttachmentsAsync(
                                    package,
                                    attachmentBackupSnapshot,
                                    cancellationToken));
                            }
                            catch (FollowUpAttachmentInstallException installException)
                            {
                                installedAttachments.AddRange(installException.Mutations);
                                attachmentRestoreFailed |= installException.RequiresFullRestore;
                                throw;
                            }
                        },
                        () => commitAttempted = true,
                        () => importCommitted = true,
                        cancellationToken);
                    return counts;
                },
                async importException =>
                {
                    if (!importCommitted && commitAttempted)
                    {
                        commitOutcomeUnknown = true;
                        logger.LogCritical(importException,
                            "FollowUp CubeDb 提交结果不确定，已禁止自动恢复附件；必须使用导入前完整备份恢复。PackageId={PackageId}",
                            state.PackageId);
                        throw new InvalidOperationException(
                            "CubeDb 提交结果不确定，已禁止自动恢复附件；必须使用本包导入前完整备份恢复数据库和附件后再继续。",
                            importException);
                    }

                    if (!ShouldRestoreAttachments(importCommitted, installedAttachments.Count > 0, commitAttempted))
                        return;

                    try
                    {
                        var skipped = await backupService.RestoreInstalledAttachmentsAsync(
                            attachmentBackupSnapshot,
                            installedAttachments,
                            CancellationToken.None);
                        if (skipped.Count > 0)
                        {
                            logger.LogWarning(
                                "FollowUp 导入失败后有 {Count} 个附件已被外部修改，未自动恢复。PackageId={PackageId}",
                                skipped.Count,
                                state.PackageId);
                        }
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
                    committedCounts = new Dictionary<string, int>(counts, StringComparer.OrdinalIgnoreCase);
                    await repository.CompleteImportAsync(
                        state.HospitalCode,
                        state.PackageId,
                        patientMappings,
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
            catch (Exception exception)
            {
                attachmentSnapshotOperationException = exception;
                throw;
            }
            finally
            {
                try
                {
                    await attachmentBackupSnapshot.DisposeAsync();
                }
                catch (Exception cleanupException)
                {
                    var cleanupFailure = new IOException(
                        "导入附件冻结快照无法清理，必须人工处置。",
                        cleanupException);
                    if (attachmentSnapshotOperationException is not null)
                        throw new AggregateException(attachmentSnapshotOperationException, cleanupFailure);
                    throw cleanupFailure;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FollowUp 包导入失败。HospitalCode={HospitalCode}, PackageId={PackageId}", state.HospitalCode, state.PackageId);
            if (importCommitted)
            {
                logger.LogCritical(ex, "FollowUp 数据和附件已提交，但写入导入成功状态或 ACK 失败，禁止降级为失败。PackageId={PackageId}", state.PackageId);
                try
                {
                    await repository.CompleteImportAsync(
                        state.HospitalCode,
                        state.PackageId,
                        patientMappings,
                        NTCareRestartRequiredCode,
                        $"{NTCareRestartRequiredMessage} 成功状态或 ACK 首次写入失败，请检查日志并重试状态同步。",
                        new
                        {
                            committed = true,
                            tables = committedCounts.Count,
                            records = committedCounts.Values.Sum(),
                            counts = committedCounts,
                            statusWriteError = ex.Message,
                            ntcareAction = "restart-required"
                        },
                        CancellationToken.None);
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
                var errorCode = ErrorCode(ex);
                var failureStatus = ResolveFailureStatus(ex, attachmentRestoreFailed, commitOutcomeUnknown);
                if (package is not null && errorCode == FollowUpErrorCodes.SchemaReviewRequired)
                {
                    try
                    {
                        await repository.SaveVerifiedAsync(
                            package,
                            CreateSchemaReviewFailureResult(ex),
                            CancellationToken.None);
                    }
                    catch (Exception schemaRecordException)
                    {
                        logger.LogError(
                            schemaRecordException,
                            "保存事务内结构复验失败结果时再次失败。PackageId={PackageId}",
                            state.PackageId);
                    }
                }
                await repository.MarkAsync(state.HospitalCode, state.PackageId, failureStatus, errorCode, ex.Message, cancellationToken: CancellationToken.None);
                var failureAckStatus = ResolveFailureAckStatus(failureStatus);
                if (package is not null && failureAckStatus is not null)
                    await EnqueueAckAsync(package, failureAckStatus, startedAt, errorCode, ex.Message, CancellationToken.None);
                await repository.AddLogAsync(state.HospitalCode, state.PackageId, "import", "Error", "FollowUp 数据包导入失败",
                    new { errorCode, failureStatus }, CancellationToken.None);
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

    internal static void ValidateVersions(
        FollowUpPackageManifest manifest,
        FollowUpPackageImportOptions options)
    {
        if (!string.Equals(options.SupportedContractVersion, FollowUpPackageImportOptions.RequiredContractVersion, StringComparison.Ordinal)
            || !string.Equals(manifest.ExportContractVersion, FollowUpPackageImportOptions.RequiredContractVersion, StringComparison.Ordinal))
            throw new FollowUpPackageException(FollowUpErrorCodes.ContractVersionUnsupported, "导出契约版本不受支持。");
        if (!string.Equals(options.ImporterVersion, FollowUpPackageImportOptions.CurrentImporterVersion, StringComparison.Ordinal)
            || !string.Equals(manifest.MinImporterVersion, FollowUpPackageImportOptions.CurrentImporterVersion, StringComparison.Ordinal))
            throw new FollowUpPackageException(FollowUpErrorCodes.ContractVersionUnsupported, "导入器版本配置或数据包最低版本与当前 v3 实现不一致。");
    }

    internal static void ValidateHospitalIdentity(
        FollowUpPackageManifest manifest,
        FollowUpPackageImportOptions options)
    {
        if (!Guid.TryParse(options.HospitalId, out var configuredHospitalId)
            || configuredHospitalId == Guid.Empty
            || configuredHospitalId != manifest.HospitalId
            || string.IsNullOrWhiteSpace(options.HospitalCode)
            || !options.HospitalCode.Equals(manifest.HospitalCode, StringComparison.Ordinal))
            throw new FollowUpPackageException(
                FollowUpErrorCodes.InvalidRequest,
                "数据包医院标识与 LHYY 当前医院配置不一致。");
    }

    internal static TargetQuestionScopeGuard? CreateTargetQuestionScopeGuard(
        FollowUpQuestionScopeSource source,
        string expectedContentHash,
        IReadOnlyCollection<string> sourceColumns)
    {
        if (source == FollowUpQuestionScopeSource.Package)
            return null;
        if (string.IsNullOrWhiteSpace(expectedContentHash) || sourceColumns.Count == 0)
            throw new FollowUpPackageException(
                FollowUpErrorCodes.SchemaReviewRequired,
                "form.form_question 缺少事务内内容复验所需的 hash 或源字段结构。");
        return new TargetQuestionScopeGuard(expectedContentHash, sourceColumns.ToArray());
    }

    internal static PackageQuestionProjectGuard? CreatePackageQuestionProjectGuard(
        PackageQuestionScopeSnapshot snapshot) =>
        snapshot.QuestionIds.Count == 0 && snapshot.ProjectIds.Count == 0
            ? null
            : new PackageQuestionProjectGuard(
                snapshot.QuestionIds.Order().ToArray(),
                snapshot.ProjectIds.Order().ToArray(),
                snapshot.PackageProjectIds.Order().ToArray());

    internal static PackageQuestionProjectGuard? CreatePackageProjectGuard(
        PackageProjectScopeSnapshot snapshot) =>
        snapshot.ProjectIds.Count == 0
            ? null
            : new PackageQuestionProjectGuard(
                [],
                snapshot.ProjectIds.Order().ToArray(),
                snapshot.ProjectIds.Order().ToArray());

    internal static bool ShouldVerifyPackageQuestionProjectScope(
        bool alreadyVerified,
        string sourceSchema,
        string sourceTable,
        bool isDynamic) =>
        !alreadyVerified
        && (isDynamic
            || sourceSchema.Equals("form", StringComparison.OrdinalIgnoreCase)
            && sourceTable.Equals("form_question", StringComparison.OrdinalIgnoreCase));

    internal static bool ShouldRestoreAttachments(
        bool importCommitted,
        bool attachmentMutationStarted,
        bool commitAttempted = false) =>
        !importCommitted && !commitAttempted && attachmentMutationStarted;

    internal static FollowUpSchemaCheckResult CreateSchemaReviewFailureResult(Exception exception) =>
        new(
            "ReviewRequired",
            "RequiresMapping",
            false,
            [exception.Message],
            [],
            []);

    internal static List<string> ResolveWriteColumns(
        FollowUpTableSchema scopedSource,
        IReadOnlySet<string> writableColumns,
        IReadOnlyCollection<string> defaultColumns)
    {
        var expected = scopedSource.Columns.Select(item => item.Name)
            .Concat(defaultColumns)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unavailable = expected.Where(item => !writableColumns.Contains(item)).ToList();
        if (unavailable.Count > 0)
            throw new FollowUpPackageException(
                FollowUpErrorCodes.SchemaReviewRequired,
                $"目标表 {scopedSource.SchemaName}.{scopedSource.TableName} 存在不可写字段：{string.Join("、", unavailable)}。");
        return expected;
    }

    internal static void EnsureProtectedQuestionProjectWritableColumns(
        FollowUpTableManifestItem targetTable,
        IReadOnlySet<string> writableColumns)
    {
        var required = FollowUpPackageSchemaCheckService.GetProtectedQuestionProjectRequiredColumns(
            targetTable.Schema,
            targetTable.TableName);
        var unavailable = required.Where(column => !writableColumns.Contains(column)).ToList();
        if (unavailable.Count > 0)
            throw new FollowUpPackageException(
                FollowUpErrorCodes.SchemaReviewRequired,
                $"目标安全表 {targetTable.Schema}.{targetTable.TableName} 存在不可写字段：{string.Join("、", unavailable)}。");
    }

    internal static string FormatSchemaCheckSummary(IReadOnlyCollection<string> messages)
    {
        if (messages.Count == 0)
            return "目标数据库结构不兼容，完整结果已记录。";
        var groups = messages
            .GroupBy(message =>
            {
                var separator = message.IndexOf('：');
                return separator > 0 ? message[..separator] : "其他结构问题";
            }, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();
        var counts = string.Join("，", groups.Select(group => $"{group.Key} {group.Count()} 项"));
        var examples = string.Join("；", groups.Select(group => group.First()).Take(5));
        return $"{counts}；示例：{examples}；完整结果已记录。";
    }

    private async Task<Dictionary<string, int>> ImportDataAsync(
        FollowUpVerifiedPackage package,
        FollowUpSchemaDecision? schemaDecision,
        IReadOnlyCollection<FollowUpTableColumnScope> columnScopes,
        TargetQuestionScopeGuard? targetQuestionScopeGuard,
        PackageQuestionProjectGuard? packageQuestionProjectGuard,
        FollowUpPatientIdentityScope patientIdentityScope,
        FollowUpPatientIdentityPlan expectedPatientIdentityPlan,
        FollowUpEdcScopePlan edcScopePlan,
        Func<Task> beforeCommit,
        Action onCommitAttempted,
        Action onCommitted,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        const string attachmentPrefix = "files/uploads/";
        var fileQuestionAttachmentPaths = package.Manifest.AttachmentFiles
            .Where(item => item.Path.StartsWith(attachmentPrefix, StringComparison.Ordinal))
            .Select(item => item.Path[attachmentPrefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(_cubeConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var patientIdentityPlan = await patientIdentityService.VerifyWithLockAsync(
                connection,
                transaction,
                patientIdentityScope,
                expectedPatientIdentityPlan,
                cancellationToken);
            if (targetQuestionScopeGuard is not null)
                await FollowUpPackageSchemaCheckService.LockTargetQuestionScopeAsync(
                    connection,
                    transaction,
                    cancellationToken);
            else if (packageQuestionProjectGuard is not null)
                await FollowUpPackageSchemaCheckService.LockPackageQuestionProjectScopeAsync(
                    connection,
                    transaction,
                    cancellationToken);
            var basePatientEventTypes = await ResolveBasePatientEventTypesAsync(
                package,
                connection,
                transaction,
                cancellationToken);
            var patientEventMappingCache =
                new Dictionary<(Guid ProjectId, string EventType), IReadOnlyList<FollowUpPatientEventFormMapping>>();
            var targetQuestionScopeVerified = false;
            var packageQuestionProjectScopeVerified = false;
            var packageProjectScopeVerifiedBeforeWrite = false;
            var ordered = OrderImportTables(package.TableManifest);
            foreach (var table in ordered)
            {
                var targetTable = FollowUpSchemaDecisionProcessor.MapManifest(table, schemaDecision);
                FollowUpPackageSchemaCheckService.ValidateProtectedQuestionProjectMapping(table, targetTable);
                FollowUpPackageSchemaCheckService.ValidateProtectedQuestionProjectColumnMappings(table, schemaDecision);
                FollowUpPackageSchemaCheckService.EnsureIdentifier(targetTable.Schema);
                FollowUpPackageSchemaCheckService.EnsureIdentifier(targetTable.TableName);
                var isDynamic = FollowUpPackageSchemaCheckService.IsMappedDynamicFormTable(table, targetTable);
                if (packageQuestionProjectGuard is not null
                    && !packageProjectScopeVerifiedBeforeWrite
                    && RequiresPackageProjectPrewriteValidation(table))
                {
                    await FollowUpPackageSchemaCheckService.EnsureExistingProjectHospitalScopeAsync(
                        connection,
                        transaction,
                        package.Manifest.HospitalId,
                        packageQuestionProjectGuard.PackageProjectIds,
                        cancellationToken);
                    packageProjectScopeVerifiedBeforeWrite = true;
                }
                if (packageQuestionProjectGuard is not null
                    && ShouldVerifyPackageQuestionProjectScope(
                        packageQuestionProjectScopeVerified,
                        table.Schema,
                        table.TableName,
                        isDynamic))
                {
                    await FollowUpPackageSchemaCheckService.EnsureExistingQuestionHospitalScopeAsync(
                        connection,
                        transaction,
                        package.Manifest.HospitalId,
                        packageQuestionProjectGuard.QuestionIds,
                        cancellationToken);
                    await FollowUpPackageSchemaCheckService.EnsureQuestionProjectScopeAsync(
                        connection,
                        transaction,
                        package.Manifest.HospitalId,
                        packageQuestionProjectGuard.ProjectIds,
                        cancellationToken);
                    packageQuestionProjectScopeVerified = true;
                }
                var identifierComparison = isDynamic ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                var originalSourceSchema = package.SchemaSnapshot.Tables.First(item =>
                    item.SchemaName.Equals(table.Schema, identifierComparison)
                    && item.TableName.Equals(table.TableName, identifierComparison));
                if (isDynamic && !targetQuestionScopeVerified)
                {
                    if (targetQuestionScopeGuard is not null)
                        await schemaCheckService.EnsureTargetQuestionContentHashAsync(
                            connection,
                            transaction,
                            package.Manifest.HospitalId,
                            targetQuestionScopeGuard.ExpectedContentHash,
                            targetQuestionScopeGuard.SourceColumns,
                            cancellationToken);
                    targetQuestionScopeVerified = true;
                }
                var columnScope = isDynamic
                    ? columnScopes.SingleOrDefault(item =>
                        item.SourceSchema.Equals(table.Schema, StringComparison.Ordinal)
                        && item.SourceTable.Equals(table.TableName, StringComparison.Ordinal))
                      ?? throw new FollowUpPackageException(
                          FollowUpErrorCodes.SchemaReviewRequired,
                          $"动态表 {table.Schema}.{table.TableName} 缺少已校验的导入字段范围。")
                    : null;
                var arrayToTextSourceColumns = columnScope?.ArrayToTextSourceColumns
                    .ToHashSet(StringComparer.Ordinal)
                    ?? new HashSet<string>(StringComparer.Ordinal);
                var sourceSchema = columnScope is null
                    ? FollowUpSchemaDecisionProcessor.MapSchema(originalSourceSchema, schemaDecision)
                    : FollowUpPackageSchemaCheckService.MapAndApplySourceTable(
                        originalSourceSchema,
                        schemaDecision,
                        columnScope);
                FollowUpPackageSchemaCheckService.ValidateProtectedQuestionProjectImportContract(
                    originalSourceSchema,
                    table,
                    sourceSchema,
                    targetTable);
                var isProtectedQuestionProjectTable =
                    FollowUpPackageSchemaCheckService.GetProtectedQuestionProjectRequiredColumns(
                        targetTable.Schema,
                        targetTable.TableName).Count > 0;
                var writableColumns = await GetWritableColumnsAsync(
                    connection,
                    transaction,
                    targetTable.Schema,
                    targetTable.TableName,
                    targetTable.ImportPolicy,
                    identifiersAreCaseSensitive: isDynamic || isProtectedQuestionProjectTable,
                    cancellationToken);
                EnsureProtectedQuestionProjectWritableColumns(targetTable, writableColumns);
                var mapping = FollowUpSchemaDecisionProcessor.FindMapping(table.Schema, table.TableName, schemaDecision);
                var defaultColumns = mapping?.DefaultValues.Keys.ToList() ?? [];
                var columns = columnScope is null
                    ? sourceSchema.Columns.Select(item => item.Name)
                        .Where(column => writableColumns.Contains(column))
                        .Concat(defaultColumns.Where(writableColumns.Contains))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : ResolveWriteColumns(sourceSchema, writableColumns, defaultColumns);
                var primaryKey = targetTable.PrimaryKey.Count > 0 ? targetTable.PrimaryKey : sourceSchema.PrimaryKey;
                var identifierComparer = isDynamic ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
                if (primaryKey.Count == 0 || primaryKey.Any(column => !columns.Contains(column, identifierComparer)))
                    throw new InvalidOperationException($"表 {targetTable.Schema}.{targetTable.TableName} 缺少可用主键，不能幂等导入。");

                var filePath = SafeStagingPath(package.StagingPath, table.ExportPath!);
                var count = 0;
                await foreach (var line in ReadRowsForImportAsync(
                                   filePath,
                                   table.FileHash,
                                   targetTable.Schema,
                                   targetTable.TableName,
                                   cancellationToken))
                {
                    FollowUpPackageSchemaCheckService.ValidateImportRow(line, arrayToTextSourceColumns);
                    var mappedLine = FollowUpSchemaDecisionProcessor.MapRow(
                        line,
                        table.Schema,
                        table.TableName,
                        schemaDecision,
                        columnScope?.SourceColumns.ToHashSet(StringComparer.Ordinal));
                    mappedLine = FollowUpTargetAdaptationService.NormalizeFileQuestionValues(
                        mappedLine,
                        columnScope?.FileQuestionTargetColumns ?? [],
                        fileQuestionAttachmentPaths);
                    var identityAdaptation = patientIdentityPlan.AdaptRow(
                        targetTable.Schema,
                        targetTable.TableName,
                        mappedLine);
                    mappedLine = identityAdaptation.Row;
                    var adaptedLine = await targetAdaptationService.AdaptRowAsync(
                        connection,
                        transaction,
                        targetTable.Schema,
                        targetTable.TableName,
                        mappedLine,
                        basePatientEventTypes,
                        patientEventMappingCache,
                        cancellationToken);
                    if (identityAdaptation.SkipWrite)
                    {
                        // 复用院端 unique_patient 或自然人匹配后的 patient，院端现有患者字段保持不变。
                    }
                    else if (table.ImportPolicy is "UseExistingById" or "RejectIfMissing")
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
            var scopeMapCount = await edcScopeService.ApplyAsync(
                connection,
                transaction,
                patientIdentityPlan.Remap(edcScopePlan),
                cancellationToken);
            if (scopeMapCount > 0)
                result["public.patient_data_scope_map"] = scopeMapCount;
            // 附件先原子替换，随后提交数据库；提交开始前失败时由上层按已安装路径恢复附件。
            await beforeCommit();
            // CommitAsync 一旦开始，客户端就无法可靠判断服务端是否已经提交，禁止再自动恢复附件。
            onCommitAttempted();
            await transaction.CommitAsync(cancellationToken);
            // 必须在 CommitAsync 成功的瞬间标记；之后即使事务/连接释放失败，也不能恢复旧附件。
            onCommitted();
            return result;
        }
        catch (Exception importException)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(importException, rollbackException);
            }
            throw;
        }
    }

    private static async IAsyncEnumerable<string> ReadRowsForImportAsync(
        string filePath,
        string? expectedFileHash,
        string schema,
        string table,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedFileHash))
            throw new InvalidDataException($"表 {schema}.{table} 缺少导入文件 hash。");
        await using var snapshot = await OpenVerifiedImportSnapshotAsync(
            filePath,
            expectedFileHash,
            cancellationToken);
        using var reader = new StreamReader(snapshot, leaveOpen: true);
        if (FollowUpImportRowOrdering.RequiresOrdering(schema, table))
        {
            var rows = new List<string>();
            string? orderedLine;
            while ((orderedLine = await reader.ReadLineAsync(cancellationToken)) is not null)
                rows.Add(orderedLine);
            foreach (var row in FollowUpImportRowOrdering.Order(schema, table, rows))
                yield return row;
            yield break;
        }

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            if (!string.IsNullOrWhiteSpace(line))
                yield return line;
    }

    internal static async Task<FileStream> OpenVerifiedImportSnapshotAsync(
        string filePath,
        string expectedFileHash,
        CancellationToken cancellationToken)
    {
        if (expectedFileHash.Length != 64 || expectedFileHash.Any(value => !Uri.IsHexDigit(value)))
            throw new InvalidDataException("导入文件 hash 必须是 64 位十六进制 SHA-256。");
        var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var actualHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
            if (!actualHash.Equals(expectedFileHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"导入文件实际 hash 与表清单不一致：{Path.GetFileName(filePath)}。");
            stream.Position = 0;
            return stream;
        }
        catch (Exception snapshotException)
        {
            try
            {
                await stream.DisposeAsync();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    snapshotException,
                    new IOException("导入文件校验失败且只读句柄无法释放。", cleanupException));
            }
            throw;
        }
    }

    private static async Task<IReadOnlyDictionary<Guid, string>> ResolveBasePatientEventTypesAsync(
        FollowUpVerifiedPackage package,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var eventIds = await ReadFormlessPatientEventIdsAsync(package, cancellationToken);
        var result = await LoadExistingBasePatientEventTypesAsync(
            connection,
            transaction,
            eventIds,
            cancellationToken);
        var packageTypes = await ReadPackageBasePatientEventTypesAsync(package, eventIds, cancellationToken);
        foreach (var (eventId, eventType) in packageTypes)
            AddBasePatientEventAssociation(result, eventId, eventType);
        return result;
    }

    private static async Task<HashSet<Guid>> ReadFormlessPatientEventIdsAsync(
        FollowUpVerifiedPackage package,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<Guid>();
        var eventTables = package.TableManifest.Where(item =>
            item.Enabled
            && !item.Skipped
            && !string.IsNullOrWhiteSpace(item.ExportPath)
            && item.Schema.Equals("care", StringComparison.OrdinalIgnoreCase)
            && item.TableName.Equals("patient_event", StringComparison.OrdinalIgnoreCase));
        foreach (var table in eventTables)
        {
            var filePath = SafeStagingPath(package.StagingPath, table.ExportPath!);
            await foreach (var line in ReadRowsForImportAsync(
                               filePath,
                               table.FileHash,
                               table.Schema,
                               table.TableName,
                               cancellationToken))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("form_set_id", out var formSetId)
                    && formSetId.ValueKind != JsonValueKind.Null)
                    continue;
                if (!root.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(id.GetString(), out var eventId)
                    || eventId == Guid.Empty)
                    throw new FollowUpPackageException(
                        FollowUpErrorCodes.SchemaReviewRequired,
                        "care.patient_event 包含无效的无表单事件 id。已阻止导入。");
                result.Add(eventId);
            }
        }
        return result;
    }

    private static async Task<Dictionary<Guid, string>> ReadPackageBasePatientEventTypesAsync(
        FollowUpVerifiedPackage package,
        IReadOnlySet<Guid> formlessEventIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        var detailTables = package.TableManifest.Where(item =>
            item.Enabled
            && !item.Skipped
            && !string.IsNullOrWhiteSpace(item.ExportPath)
            && item.Schema.Equals("care", StringComparison.OrdinalIgnoreCase)
            && item.TableName is "patient_hospitalized" or "patient_outpatient");
        foreach (var table in detailTables)
        {
            var filePath = SafeStagingPath(package.StagingPath, table.ExportPath!);
            await foreach (var line in ReadRowsForImportAsync(
                               filePath,
                               table.FileHash,
                               table.Schema,
                               table.TableName,
                               cancellationToken))
            {
                using var document = JsonDocument.Parse(line);
                if (!document.RootElement.TryGetProperty("patient_event_id", out var value)
                    || value.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(value.GetString(), out var eventId)
                    || eventId == Guid.Empty)
                    throw new FollowUpPackageException(
                        FollowUpErrorCodes.SchemaReviewRequired,
                        $"表 {table.Schema}.{table.TableName} 包含无效的 patient_event_id。已阻止导入。");
                if (!formlessEventIds.Contains(eventId))
                    continue;
                var eventType = table.TableName.Equals("patient_hospitalized", StringComparison.OrdinalIgnoreCase)
                    ? "住院"
                    : "门诊";
                AddBasePatientEventAssociation(result, eventId, eventType);
            }
        }
        return result;
    }

    private static async Task<Dictionary<Guid, string>> LoadExistingBasePatientEventTypesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlySet<Guid> eventIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        if (eventIds.Count == 0)
            return result;

        await using var command = new NpgsqlCommand(BuildExistingBasePatientEventTypesSql(), connection, transaction);
        command.Parameters.AddWithValue("event_ids", eventIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            AddBasePatientEventAssociation(result, reader.GetGuid(0), reader.GetString(1));
        return result;
    }

    internal static string BuildExistingBasePatientEventTypesSql() => """
        SELECT patient_event_id, '住院'
        FROM care.patient_hospitalized
        WHERE patient_event_id = ANY(@event_ids)
        UNION ALL
        SELECT patient_event_id, '门诊'
        FROM care.patient_outpatient
        WHERE patient_event_id = ANY(@event_ids)
        """;

    internal static void AddBasePatientEventAssociation(
        IDictionary<Guid, string> associations,
        Guid eventId,
        string eventType)
    {
        if (associations.TryGetValue(eventId, out var existingType)
            && !existingType.Equals(eventType, StringComparison.Ordinal))
            throw new FollowUpPackageException(
                FollowUpErrorCodes.SchemaReviewRequired,
                $"患者事件 {eventId} 同时关联住院和门诊明细。已阻止导入。");
        associations[eventId] = eventType;
    }

    private static async Task<HashSet<string>> GetWritableColumnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        string importPolicy,
        bool identifiersAreCaseSensitive,
        CancellationToken cancellationToken)
    {
        var privilegePredicate = FollowUpImportPolicyPermissions.BuildColumnPrivilegePredicate(importPolicy);
        await using var command = new NpgsqlCommand($"""
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
              AND is_generated = 'NEVER'
              AND (is_identity = 'NO' OR identity_generation IS DISTINCT FROM 'ALWAYS')
              {privilegePredicate}
            """, connection, transaction);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        var result = new HashSet<string>(
            identifiersAreCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
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
                    sourceMarker = "lhyy.followup_patient_identity_map@DataSyncDb"
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
    internal static string ResolveFailureStatus(
        Exception exception,
        bool attachmentRestoreFailed,
        bool commitOutcomeUnknown = false)
    {
        if (attachmentRestoreFailed || commitOutcomeUnknown)
            return "RestoreFailed";
        return ErrorCode(exception) == FollowUpErrorCodes.SchemaReviewRequired
            ? "WaitingForDecision"
            : "ImportFailed";
    }

    internal static string? ResolveFailureAckStatus(string failureStatus) =>
        failureStatus == "WaitingForDecision" ? null : "ImportFailed";

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

    internal static bool RequiresPackageProjectPrewriteValidation(FollowUpTableManifestItem source) =>
        source.Schema.Equals("form", StringComparison.OrdinalIgnoreCase)
        && source.TableName.Equals("form_project", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<FollowUpTableManifestItem> OrderImportTables(
        IReadOnlyList<FollowUpTableManifestItem> manifest)
    {
        var ordered = manifest
            .Select((item, index) => (Item: item, Index: index))
            .Where(entry => entry.Item.Enabled
                            && !entry.Item.Skipped
                            && !string.IsNullOrWhiteSpace(entry.Item.ExportPath))
            .OrderBy(entry => CategoryOrder(entry.Item.DataCategory))
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToList();
        var projectIndex = ordered.FindIndex(RequiresPackageProjectPrewriteValidation);
        if (projectIndex >= 0)
        {
            var earlyConsumers = ordered
                .Take(projectIndex)
                .Where(IsPackageQuestionConsumer)
                .ToList();
            if (earlyConsumers.Count > 0)
            {
                foreach (var consumer in earlyConsumers)
                    ordered.Remove(consumer);
                projectIndex = ordered.FindIndex(RequiresPackageProjectPrewriteValidation);
                ordered.InsertRange(projectIndex + 1, earlyConsumers);
            }
        }
        return ordered;
    }

    private static bool IsPackageQuestionConsumer(FollowUpTableManifestItem item) =>
        (item.Schema.Equals("form", StringComparison.OrdinalIgnoreCase)
         && item.TableName.Equals("form_question", StringComparison.OrdinalIgnoreCase))
        || FollowUpPackageSchemaCheckService.IsDynamicFormTable(item);

    private static int CategoryOrder(string category) => category switch { "ReferenceMaster" => 0, "Relationship" => 1, "BusinessData" => 2, _ => 3 };
    private static string ErrorCode(Exception exception) => exception is FollowUpPackageException package ? package.ErrorCode : FollowUpErrorCodes.InternalError;
}
