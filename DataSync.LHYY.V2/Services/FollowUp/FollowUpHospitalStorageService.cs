using System.Security.Cryptography;
using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using Microsoft.Extensions.Options;

namespace DataSync.LHYY.V2.Services.FollowUp;

public sealed class FollowUpHospitalStorageService(
    FollowUpPackageImportRepository repository,
    FollowUpCubeOperationCoordinator operationCoordinator,
    FollowUpPackageBackupService backupService,
    FollowUpStorageCleanupManifestStore manifestStore,
    IOptions<FollowUpPackageImportOptions> options,
    ILogger<FollowUpHospitalStorageService> logger)
{
    private readonly FollowUpPackageImportOptions _options = options.Value;
    private readonly FollowUpStorageCleanupManifestStore _manifestStore = manifestStore;

    public List<FollowUpStorageStatus> GetStorageStatus() =>
    [
        FollowUpStorageInspector.Inspect("共享包仓库", _options.PackageRoot,
            _options.StorageWarningUsedPercent, _options.StorageCriticalUsedPercent),
        FollowUpStorageInspector.Inspect("临时解包目录", _options.StagingRoot,
            _options.StorageWarningUsedPercent, _options.StorageCriticalUsedPercent),
        FollowUpStorageInspector.Inspect("导入前备份", _options.BackupRoot,
            _options.StorageWarningUsedPercent, _options.StorageCriticalUsedPercent),
        FollowUpStorageInspector.Inspect("业务附件卷", _options.AttachmentRoot,
            _options.StorageWarningUsedPercent, _options.StorageCriticalUsedPercent, calculateDirectoryBytes: false)
    ];

    public async Task<FollowUpImportOperationResult> CleanupAsync(
        FollowUpPackageImportState state,
        string operatorName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operatorName))
            return new FollowUpImportOperationResult(false, "请填写操作人后再执行清理。");

        await using var operationLease = await operationCoordinator.TryAcquireExclusiveAsync(cancellationToken);
        if (operationLease is null)
            return new FollowUpImportOperationResult(false, "当前存在导入、备份或恢复操作，不能执行存储清理。");
        await using var packageLease = await repository.TryAcquireStorageCleanupPackageLockAsync(
            state.HospitalCode, state.PackageId, cancellationToken);
        if (packageLease is null)
            return new FollowUpImportOperationResult(false, "该数据包正在拉取或清理，请稍后重试。");

        var existing = await _manifestStore.ReadAsync(state.HospitalCode, state.PackageId, cancellationToken);
        if (existing is not null && !await ReconcileOneAsync(existing, cancellationToken))
            return new FollowUpImportOperationResult(false, "上次清理尚未完成自动恢复，请先处理清理操作清单中的残留文件。");

        var manifest = new FollowUpStorageCleanupManifest
        {
            HospitalCode = state.HospitalCode,
            PackageId = state.PackageId,
            OperatorName = operatorName.Trim(),
            Phase = FollowUpStorageCleanupPhase.Requested
        };
        await _manifestStore.WriteAsync(manifest, cancellationToken);

        try
        {
            var candidate = await repository.PrepareStorageCleanupAsync(
                state.HospitalCode,
                state.PackageId,
                async preparedCandidate =>
                {
                    manifest.Candidate = preparedCandidate;
                    manifest.Items = BuildManifestItems(preparedCandidate, manifest.OperationId);
                    ValidateManifestStructure(manifest);
                    manifest.Phase = FollowUpStorageCleanupPhase.MovingFiles;
                    await _manifestStore.WriteAsync(manifest, CancellationToken.None);
                    foreach (var item in manifest.Items) MoveToQuarantine(item);
                    manifest.Phase = FollowUpStorageCleanupPhase.FilesQuarantined;
                    await _manifestStore.WriteAsync(manifest, CancellationToken.None);
                    await ValidateQuarantinedCandidateAsync(
                        preparedCandidate,
                        manifest.Items,
                        CancellationToken.None);
                },
                cancellationToken);

            try
            {
                await repository.CompleteStorageCleanupAsync(candidate, manifest.OperatorName, cancellationToken);
            }
            catch
            {
                // A transport error can happen after COMMIT reached PostgreSQL. Never restore a file
                // until the authoritative database state has been read on a new connection.
                if (await repository.GetStorageCleanupDatabaseStateAsync(
                        candidate.HospitalCode, candidate.PackageId, CancellationToken.None)
                    != FollowUpStorageCleanupDatabaseState.Archived)
                    throw;
            }

            manifest.Phase = FollowUpStorageCleanupPhase.DatabaseArchived;
            await _manifestStore.WriteAsync(manifest, CancellationToken.None);
            ValidateManifestStructure(manifest);
            var residue = FollowUpStorageCleanupFileRecovery.DeleteQuarantine(manifest.Items);
            var originalResidue = residue
                .Where(path => manifest.Items.Any(item => item.OriginalPath.Equals(path, PathComparison)))
                .ToArray();
            if (residue.Count == 0) _manifestStore.Delete(manifest.HospitalCode, manifest.PackageId);
            else
                await repository.AddLogAsync(candidate.HospitalCode, candidate.PackageId,
                    originalResidue.Length == 0 ? "storage-cleanup-residue" : "storage-cleanup-manual-review",
                    "Error",
                    originalResidue.Length == 0
                        ? "业务记录已归档，但隔离文件删除失败"
                        : "业务记录已归档，但规范原路径重新出现，必须人工处理",
                    new { residue }, CancellationToken.None);

            return new FollowUpImportOperationResult(originalResidue.Length == 0, residue.Count == 0
                ? "旧包文件和对应备份已安全清理，链路与审计记录已保留。"
                : originalResidue.Length > 0
                    ? $"包与备份已归档，但有 {originalResidue.Length} 个规范原路径重新出现；已保留清理清单，必须人工处理。"
                    : $"包与备份已归档，但有 {residue.Count} 个隔离项将在后台重试删除。");
        }
        catch (Exception ex)
        {
            var recovered = await ReconcileAfterFailureAsync(manifest);
            logger.LogWarning(ex, "FollowUp 医院端存储清理被拒绝或失败。PackageId={PackageId}", state.PackageId);
            return new FollowUpImportOperationResult(false, recovered
                ? ex.Message
                : $"{ex.Message}；清理结果暂时不确定，已保留持久化操作清单，后台将继续核对恢复。");
        }
    }

    public async Task ReconcilePendingAsync(CancellationToken cancellationToken = default)
    {
        foreach (var manifest in await _manifestStore.ReadAllAsync(cancellationToken))
        {
            await using var operationLease = await operationCoordinator.TryAcquireExclusiveAsync(cancellationToken);
            if (operationLease is null) return;
            await using var packageLease = await repository.TryAcquireStorageCleanupPackageLockAsync(
                manifest.HospitalCode, manifest.PackageId, cancellationToken);
            if (packageLease is null) continue;
            try { await ReconcileOneAsync(manifest, cancellationToken); }
            catch (Exception ex)
            {
                logger.LogError(ex, "自动恢复存储清理操作失败。OperationId={OperationId}", manifest.OperationId);
            }
        }
    }

    private async Task<bool> ReconcileAfterFailureAsync(FollowUpStorageCleanupManifest manifest)
    {
        try { return await ReconcileOneAsync(manifest, CancellationToken.None); }
        catch (Exception recoveryEx)
        {
            logger.LogError(recoveryEx, "核对并恢复存储清理失败。OperationId={OperationId}", manifest.OperationId);
            return false;
        }
    }

    private async Task<bool> ReconcileOneAsync(
        FollowUpStorageCleanupManifest manifest,
        CancellationToken cancellationToken)
    {
        if (manifest.Candidate is null)
        {
            var emptyRequestState = await repository.GetStorageCleanupDatabaseStateAsync(
                manifest.HospitalCode, manifest.PackageId, cancellationToken);
            if (manifest.Version == 1
                && manifest.Phase == FollowUpStorageCleanupPhase.Requested
                && manifest.Items.Count == 0
                && emptyRequestState == FollowUpStorageCleanupDatabaseState.Original)
            {
                _manifestStore.Delete(manifest.HospitalCode, manifest.PackageId);
                return true;
            }
            return false;
        }

        ValidateManifestStructure(manifest);
        var databaseState = await repository.GetStorageCleanupDatabaseStateAsync(
            manifest.HospitalCode, manifest.PackageId, cancellationToken);
        var recoveryAction = FollowUpStorageCleanupRecoveryPolicy.Decide(databaseState);
        if (recoveryAction == FollowUpStorageCleanupRecoveryAction.DeleteQuarantine)
        {
            manifest.Phase = FollowUpStorageCleanupPhase.DatabaseArchived;
            await _manifestStore.WriteAsync(manifest, cancellationToken);
            var residue = FollowUpStorageCleanupFileRecovery.DeleteQuarantine(manifest.Items);
            if (residue.Count == 0) _manifestStore.Delete(manifest.HospitalCode, manifest.PackageId);
            return residue.Count == 0;
        }

        if (recoveryAction == FollowUpStorageCleanupRecoveryAction.StopForManualReview)
            return false;

        var restoreErrors = FollowUpStorageCleanupFileRecovery.Restore(manifest.Items);
        if (restoreErrors.Count > 0) return false;
        // 隔离项可能在进程中断后被替换成链接；移动完成后必须再次确认规范原路径仍受控。
        ValidateManifestStructure(manifest);
        await ValidateRestoredCandidateAsync(manifest, cancellationToken);
        if (recoveryAction == FollowUpStorageCleanupRecoveryAction.RestoreFilesAndCancelDatabase)
        {
            if (manifest.Candidate is null) return false;
            await repository.CancelStorageCleanupAsync(manifest.Candidate,
                "进程中断后自动恢复清理状态", cancellationToken);
        }
        _manifestStore.Delete(manifest.HospitalCode, manifest.PackageId);
        return true;
    }

    private void ValidateManifestStructure(FollowUpStorageCleanupManifest manifest)
    {
        if (manifest.Version != 1
            || !Guid.TryParseExact(manifest.OperationId, "N", out _)
            || manifest.Candidate is null
            || !string.Equals(manifest.HospitalCode, manifest.Candidate.HospitalCode, StringComparison.Ordinal)
            || !string.Equals(manifest.PackageId, manifest.Candidate.PackageId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("存储清理操作清单版本、标识或候选记录无效。");
        }

        var candidate = manifest.Candidate;
        var packagePath = FollowUpStorageInspector.ValidateManagedFile(
            _options.PackageRoot, candidate.PackagePath, ".fupkg");
        var canonicalPackagePath = Path.GetFullPath(Path.Combine(_options.PackageRoot, $"{candidate.PackageId}.fupkg"));
        if (!packagePath.Equals(canonicalPackagePath, PathComparison))
            throw new InvalidDataException("存储清理操作清单中的数据包路径不是规范路径。");

        foreach (var backup in candidate.Backups)
        {
            var expectedRoot = Path.GetFullPath(Path.Combine(_options.BackupRoot, candidate.HospitalCode,
                candidate.PackageId, backup.RecordId.ToString("N")));
            var actualRoot = FollowUpStorageInspector.ValidateManagedDirectory(_options.BackupRoot, backup.RootPath);
            if (!actualRoot.Equals(expectedRoot, PathComparison))
                throw new InvalidDataException("存储清理操作清单中的备份目录不是规范路径。");
            FollowUpStorageInspector.ValidateManagedFile(actualRoot, backup.DatabaseBackupPath, ".dump");
            FollowUpStorageInspector.ValidateManagedDirectory(actualRoot, backup.AttachmentBackupPath);
        }

        var expectedItems = BuildManifestItems(candidate, manifest.OperationId);
        if (manifest.Items.Count != expectedItems.Count
            || manifest.Items.Zip(expectedItems).Any(pair =>
                pair.First.IsDirectory != pair.Second.IsDirectory
                || !pair.First.OriginalPath.Equals(pair.Second.OriginalPath, PathComparison)
                || !pair.First.QuarantinePath.Equals(pair.Second.QuarantinePath, PathComparison)))
        {
            throw new InvalidDataException("存储清理操作清单中的隔离路径与候选记录不一致。");
        }

        foreach (var item in manifest.Items)
            ValidateQuarantinePath(item);
    }

    private void ValidateQuarantinePath(FollowUpStorageCleanupManifestItem item)
    {
        if (item.IsDirectory)
        {
            FollowUpStorageInspector.ValidateManagedDirectory(_options.BackupRoot, item.QuarantinePath);
            return;
        }

        FollowUpStorageInspector.ValidateManagedFile(
            _options.PackageRoot,
            item.QuarantinePath,
            Path.GetExtension(item.QuarantinePath));
    }

    private async Task ValidateQuarantinedCandidateAsync(
        FollowUpStorageCleanupCandidate candidate,
        IReadOnlyList<FollowUpStorageCleanupManifestItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count != candidate.Backups.Count + 1)
            throw new InvalidDataException("隔离项数量与清理候选记录不一致。");
        foreach (var item in items)
            ValidateQuarantinePath(item);
        await ValidateCandidateAtPathsAsync(
            candidate,
            items[0].QuarantinePath,
            items.Skip(1).Select(item => item.QuarantinePath).ToArray(),
            cancellationToken);
    }

    private async Task ValidateRestoredCandidateAsync(
        FollowUpStorageCleanupManifest manifest,
        CancellationToken cancellationToken)
    {
        var candidate = manifest.Candidate
                        ?? throw new InvalidDataException("存储清理操作清单缺少候选记录。");
        foreach (var item in manifest.Items)
        {
            var originalExists = item.IsDirectory
                ? Directory.Exists(item.OriginalPath)
                : File.Exists(item.OriginalPath);
            if (!originalExists || File.Exists(item.QuarantinePath) || Directory.Exists(item.QuarantinePath))
                throw new InvalidDataException("清理对象没有完整恢复到规范原路径，拒绝取消数据库准备态。");
        }

        await ValidateCandidateAtPathsAsync(
            candidate,
            manifest.Items[0].OriginalPath,
            manifest.Items.Skip(1).Select(item => item.OriginalPath).ToArray(),
            cancellationToken);
    }

    private async Task ValidateCandidateAtPathsAsync(
        FollowUpStorageCleanupCandidate candidate,
        string packagePath,
        IReadOnlyList<string> backupRootPaths,
        CancellationToken cancellationToken)
    {
        if (backupRootPaths.Count != candidate.Backups.Count)
            throw new InvalidDataException("备份路径数量与清理候选记录不一致。");
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("待校验的数据包文件不存在。", packagePath);
        if (!string.Equals(
                await HashFileAsync(packagePath, cancellationToken),
                candidate.PackageHash,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("待校验的数据包 hash 与导入记录不一致。");

        for (var index = 0; index < candidate.Backups.Count; index++)
            await ValidateBackupAtRootAsync(
                candidate.Backups[index],
                backupRootPaths[index],
                cancellationToken);
    }

    private async Task ValidateBackupAtRootAsync(
        FollowUpStorageCleanupBackup backup,
        string backupRootPath,
        CancellationToken cancellationToken)
    {
        var originalRoot = Path.GetFullPath(backup.RootPath);
        var root = FollowUpStorageInspector.ValidateManagedDirectory(
            _options.BackupRoot,
            backupRootPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"待校验的备份目录不存在：{root}");
        var databasePath = FollowUpStorageInspector.ValidateManagedFile(
            root,
            RebaseQuarantinedPath(originalRoot, root, backup.DatabaseBackupPath),
            ".dump");
        var attachmentPath = FollowUpStorageInspector.ValidateManagedDirectory(
            root,
            RebaseQuarantinedPath(originalRoot, root, backup.AttachmentBackupPath));
        if (!File.Exists(databasePath))
            throw new InvalidDataException("待校验的备份数据库缺失，拒绝静默清理。");
        if (!string.Equals(await HashFileAsync(databasePath, cancellationToken), backup.Hash,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("待校验的备份数据库 hash 校验失败，拒绝清理。");
        await backupService.ValidateRegisteredAttachmentBackupAsync(
            new FollowUpBackupArtifact(
                backup.RecordId,
                root,
                databasePath,
                attachmentPath,
                backup.Hash,
                backup.SizeBytes,
                backup.AttachmentManifestHash,
                backup.AttachmentEntryCount),
            afterManifestRead: null,
            cancellationToken);
    }

    private static string RebaseQuarantinedPath(
        string originalRoot,
        string quarantineRoot,
        string originalPath)
    {
        var relativePath = Path.GetRelativePath(
            Path.GetFullPath(originalRoot),
            Path.GetFullPath(originalPath));
        if (Path.IsPathRooted(relativePath)
            || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidDataException("备份子路径无法安全映射到隔离目录。");
        return Path.GetFullPath(Path.Combine(quarantineRoot, relativePath));
    }

    private static List<FollowUpStorageCleanupManifestItem> BuildManifestItems(
        FollowUpStorageCleanupCandidate candidate,
        string operationId)
    {
        var result = new List<FollowUpStorageCleanupManifestItem>
        {
            new()
            {
                OriginalPath = Path.GetFullPath(candidate.PackagePath),
                QuarantinePath = Path.GetFullPath(candidate.PackagePath) + $".cleanup-{operationId}",
                IsDirectory = false
            }
        };
        result.AddRange(candidate.Backups.Select(backup => new FollowUpStorageCleanupManifestItem
        {
            OriginalPath = Path.GetFullPath(backup.RootPath),
            QuarantinePath = Path.GetFullPath(backup.RootPath) + $".cleanup-{operationId}",
            IsDirectory = true
        }));
        return result;
    }

    private static void MoveToQuarantine(FollowUpStorageCleanupManifestItem item)
    {
        if (item.IsDirectory) Directory.Move(item.OriginalPath, item.QuarantinePath);
        else File.Move(item.OriginalPath, item.QuarantinePath);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

public sealed class FollowUpStorageCleanupReconciliationWorker(
    IServiceProvider serviceProvider,
    ILogger<FollowUpStorageCleanupReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                await scope.ServiceProvider.GetRequiredService<FollowUpHospitalStorageService>()
                    .ReconcilePendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "启动/后台恢复存储清理操作失败，将继续重试。"); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
