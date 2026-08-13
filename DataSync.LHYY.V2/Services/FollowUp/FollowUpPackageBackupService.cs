using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using Microsoft.Extensions.Options;
using Npgsql;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace DataSync.LHYY.V2.Services.FollowUp;

public sealed class FollowUpPackageBackupService(
    IConfiguration configuration,
    IOptions<FollowUpPackageImportOptions> options)
{
    private const int AttachmentBackupManifestVersion = 2;
    private const string AttachmentBackupManifestFileName = "attachment-backup.json";
    private const string LegacyAttachmentHashBaselineFileName = "attachment-backup.hash-baseline.v2.json";
    private const string LegacyArtifactAnchorFileName = "attachment-backup.artifact-anchor.v2.json";
    internal const string AttachmentBackupMetadataStagingDirectoryName = ".attachment-backup-metadata-staging";
    private readonly string _cubeConnectionString = configuration.GetConnectionString("CubeDb")
        ?? throw new InvalidOperationException("未找到连接字符串 'CubeDb'");
    private readonly FollowUpPackageImportOptions _options = options.Value;

    public bool PostgreSqlToolsReady => FindExecutable("pg_dump") is not null && FindExecutable("pg_restore") is not null;

    public async Task<FollowUpBackupArtifact> CreateAsync(FollowUpVerifiedPackage package, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var plannedAttachments = package.Manifest.AttachmentFiles
            .Select(attachment => (Attachment: attachment, RelativePath: NormalizeAttachmentPath(attachment.Path)))
            .ToList();
        var duplicatePath = plannedAttachments
            .GroupBy(item => item.RelativePath, pathComparer)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicatePath is not null)
            throw new InvalidDataException($"附件清单包含重复路径：{duplicatePath.Key}");
        var root = Path.Combine(Path.GetFullPath(_options.BackupRoot), package.Manifest.HospitalCode, package.Manifest.PackageId, id.ToString("N"));
        var databasePath = Path.Combine(root, "database.dump");
        var attachmentPath = Path.Combine(root, "attachments");
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(attachmentPath);
            RestrictDirectory(root);
            await RunPostgreSqlToolAsync("pg_dump", databasePath, restore: false, cancellationToken);

            var entries = new List<AttachmentBackupEntry>();
            foreach (var planned in plannedAttachments)
            {
                var attachment = planned.Attachment;
                var relative = planned.RelativePath;
                var source = SafeCombine(_options.AttachmentRoot, relative);
                var backup = SafeCombine(attachmentPath, relative);
                var existed = File.Exists(source);
                string? attachmentHash = null;
                if (existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(source, backup, overwrite: false);
                    attachmentHash = await HashFileAsync(backup, cancellationToken);
                }
                entries.Add(new AttachmentBackupEntry(relative, existed, attachmentHash));
            }
            var manifestPath = GetBackupMetadataPath(attachmentPath, AttachmentBackupManifestFileName);
            var manifest = new AttachmentBackupManifest(AttachmentBackupManifestVersion, entries);
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, FollowUpJson.Options), cancellationToken);
            var hash = await HashFileAsync(databasePath, cancellationToken);
            var attachmentManifestHash = await HashFileAsync(manifestPath, cancellationToken);
            var size = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            return new FollowUpBackupArtifact(
                id,
                root,
                databasePath,
                attachmentPath,
                hash,
                size,
                attachmentManifestHash,
                entries.Count);
        }
        catch (Exception backupException)
        {
            if (Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(
                        backupException,
                        new IOException("备份创建失败且临时备份目录无法清理，必须人工处置。", cleanupException));
                }
            }
            throw;
        }
    }

    public async Task RestoreAsync(FollowUpBackupArtifact artifact, CancellationToken cancellationToken = default)
    {
        FollowUpAttachmentBackupSnapshot? attachmentSnapshot = null;
        FollowUpDatabaseRestoreSnapshot? databaseSnapshot = null;
        Exception? restoreException = null;
        try
        {
            // pg_restore 是破坏性操作；数据库和全部附件都必须先复制、校验并冻结到备份根之外。
            attachmentSnapshot = await CreateValidatedAttachmentSnapshotAsync(artifact, cancellationToken);
            databaseSnapshot = await CreateValidatedDatabaseSnapshotAsync(artifact, cancellationToken);
            await RunPostgreSqlToolAsync("pg_restore", databaseSnapshot.FilePath, restore: true, cancellationToken);
            await RestoreAttachmentsAsync(
                attachmentSnapshot.AttachmentBackupPath,
                attachmentSnapshot.Entries,
                beforeBackupCopy: null,
                cancellationToken);
        }
        catch (Exception exception)
        {
            restoreException = exception;
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            if (databaseSnapshot is not null
                && (File.Exists(databaseSnapshot.FilePath) || Directory.Exists(databaseSnapshot.WorkRoot)))
            {
                try
                {
                    DeleteDatabaseSnapshot(databaseSnapshot);
                }
                catch (Exception cleanupException)
                {
                    cleanupErrors.Add(new IOException(
                        "数据库恢复临时快照无法清理，必须人工处置。",
                        cleanupException));
                }
            }
            if (attachmentSnapshot is not null)
            {
                try
                {
                    await attachmentSnapshot.DisposeAsync();
                }
                catch (Exception cleanupException)
                {
                    cleanupErrors.Add(new IOException(
                        "附件恢复临时快照无法清理，必须人工处置。",
                        cleanupException));
                }
            }

            if (cleanupErrors.Count > 0)
            {
                if (restoreException is null)
                    throw new FollowUpRestoreCleanupException(cleanupErrors);
                throw new AggregateException([restoreException, .. cleanupErrors]);
            }
        }

        if (restoreException is not null)
            ExceptionDispatchInfo.Capture(restoreException).Throw();
    }

    internal async Task<FollowUpAttachmentBackupSnapshot> CreateValidatedAttachmentSnapshotAsync(
        FollowUpBackupArtifact artifact,
        CancellationToken cancellationToken)
    {
        var entries = await LoadRegisteredAttachmentBackupAsync(
            artifact,
            afterManifestRead: null,
            cancellationToken);
        FollowUpAttachmentBackupSnapshot? snapshot = null;
        string? workRoot = null;
        Exception? snapshotException = null;
        try
        {
            EnsureSnapshotPathOutsideBackupRoot(
                Path.GetTempPath(),
                "系统临时目录必须位于 BackupRoot 之外，才能创建附件恢复快照。");
            workRoot = Directory.CreateTempSubdirectory("datasync-followup-attachments-").FullName;
            EnsureSnapshotPathOutsideBackupRoot(
                workRoot,
                "附件恢复临时快照必须位于 BackupRoot 之外。");
            RestrictDirectory(workRoot);
            var frozenAttachmentPath = Path.Combine(workRoot, "attachments");
            Directory.CreateDirectory(frozenAttachmentPath);
            RestrictDirectory(frozenAttachmentPath);
            snapshot = new FollowUpAttachmentBackupSnapshot(
                frozenAttachmentPath,
                workRoot,
                entries);
            foreach (var entry in entries.Where(entry => entry.Existed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = SafeCombine(artifact.AttachmentBackupPath, entry.RelativePath);
                var frozen = SafeCombine(frozenAttachmentPath, entry.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(frozen)!);
                File.Copy(source, frozen, overwrite: false);
                await ValidateAttachmentBackupEntryAsync(entry, frozen, cancellationToken);
            }
            return snapshot;
        }
        catch (Exception exception)
        {
            snapshotException = exception;
            throw;
        }
        finally
        {
            if (snapshotException is not null && (snapshot is not null || workRoot is not null))
            {
                try
                {
                    if (snapshot is not null)
                        await snapshot.DisposeAsync();
                    else if (Directory.Exists(workRoot))
                        Directory.Delete(workRoot, recursive: true);
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(
                        snapshotException,
                        new IOException("附件恢复临时快照创建失败且无法清理，必须人工处置。", cleanupException));
                }
            }
        }
    }

    internal async Task<IReadOnlyList<string>> ValidateRegisteredAttachmentBackupAsync(
        FollowUpBackupArtifact artifact,
        Action<string>? afterManifestRead,
        CancellationToken cancellationToken)
    {
        var entries = await LoadRegisteredAttachmentBackupAsync(
            artifact,
            afterManifestRead,
            cancellationToken);
        return entries.Select(entry => entry.RelativePath).ToArray();
    }

    private async Task<IReadOnlyList<AttachmentBackupEntry>> LoadRegisteredAttachmentBackupAsync(
        FollowUpBackupArtifact artifact,
        Action<string>? afterManifestRead,
        CancellationToken cancellationToken)
    {
        ValidateArtifactLayout(artifact);
        var hasManifestHash = !string.IsNullOrWhiteSpace(artifact.AttachmentManifestHash);
        var hasEntryCount = artifact.AttachmentEntryCount.HasValue;
        if (hasManifestHash != hasEntryCount)
            throw new InvalidDataException("附件备份登记的清单 hash 与条目数不完整。");
        var manifestPath = GetRegisteredAttachmentBackupManifestPath(
            artifact.AttachmentBackupPath,
            requireExternalManifest: hasManifestHash);
        EnsurePathHasNoLinks(Path.GetFullPath(artifact.RootPath), manifestPath);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("附件备份清单不存在。", manifestPath);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        afterManifestRead?.Invoke(manifestPath);
        var registration = await ResolveAttachmentBackupRegistrationAsync(
            artifact,
            manifestBytes,
            cancellationToken);
        if (!string.Equals(
                HashBytes(manifestBytes),
                registration.ManifestHash,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("附件备份清单 hash 与登记值不一致。");
        if (ComputeRegisteredBackupSize(artifact) != artifact.SizeBytes)
            throw new InvalidDataException("附件清单或备份文件集合与登记的备份大小不一致。");
        IReadOnlyList<AttachmentBackupEntry> entries;
        try
        {
            entries = await LoadAndValidateAttachmentBackupAsync(
                artifact.AttachmentBackupPath,
                manifestBytes,
                cancellationToken);
        }
        catch (InvalidDataException exception) when (
            registration.RequiresReview && IsBackupReviewRequired(exception))
        {
            await PublishLegacyArtifactAnchorAsync(
                artifact,
                registration.CandidateAnchor!,
                cancellationToken);
            throw BackupReviewRequired(
                $"旧备份记录缺少附件清单登记信息，且清单条目缺少 hash；已生成 {LegacyArtifactAnchorFileName} 和 {LegacyAttachmentHashBaselineFileName}。本次未执行恢复，请人工核对两份信任锚点后再次执行。",
                exception);
        }
        if (entries.Count != registration.EntryCount)
            throw new InvalidDataException("附件备份清单条目数与登记值不一致。");
        if (registration.RequiresReview)
        {
            await PublishLegacyArtifactAnchorAsync(
                artifact,
                registration.CandidateAnchor!,
                cancellationToken);
            throw BackupReviewRequired(
                $"旧备份记录缺少附件清单 hash/条目数，已生成 {LegacyArtifactAnchorFileName}；本次未执行恢复。请人工核对信任锚点后再次执行。",
                innerException: null);
        }
        return entries;
    }

    internal async Task<FollowUpDatabaseRestoreSnapshot> CreateValidatedDatabaseSnapshotAsync(
        FollowUpBackupArtifact artifact,
        CancellationToken cancellationToken)
    {
        ValidateArtifactLayout(artifact);
        if (!File.Exists(artifact.DatabaseBackupPath))
            throw new FileNotFoundException("数据库备份文件不存在。", artifact.DatabaseBackupPath);
        FollowUpDatabaseRestoreSnapshot? snapshot = null;
        try
        {
            EnsureSnapshotPathOutsideBackupRoot(
                Path.GetTempPath(),
                "系统临时目录必须位于 BackupRoot 之外，才能创建数据库恢复快照。");
            var workRoot = Directory.CreateTempSubdirectory("datasync-followup-db-").FullName;
            snapshot = new FollowUpDatabaseRestoreSnapshot(
                Path.Combine(workRoot, "database.dump"),
                workRoot);
            EnsureSnapshotPathOutsideBackupRoot(
                workRoot,
                "数据库恢复临时快照必须位于 BackupRoot 之外。");
            RestrictDirectory(workRoot);
            File.Copy(artifact.DatabaseBackupPath, snapshot.FilePath, overwrite: false);
            if (!string.Equals(
                    await HashFileAsync(snapshot.FilePath, cancellationToken),
                    artifact.Hash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("数据库备份临时快照 hash 校验失败。");
            return snapshot;
        }
        catch (Exception exception)
        {
            if (snapshot is not null
                && (File.Exists(snapshot.FilePath) || Directory.Exists(snapshot.WorkRoot)))
            {
                try
                {
                    DeleteDatabaseSnapshot(snapshot);
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(
                        exception,
                        new IOException("数据库恢复临时快照创建失败且无法清理，必须人工处置。", cleanupException));
                }
            }
            throw;
        }
    }

    private static void DeleteDatabaseSnapshot(FollowUpDatabaseRestoreSnapshot snapshot)
    {
        if (File.Exists(snapshot.FilePath)) File.Delete(snapshot.FilePath);
        if (Directory.Exists(snapshot.WorkRoot)) Directory.Delete(snapshot.WorkRoot, recursive: false);
    }

    public async Task RestoreAttachmentsAsync(string attachmentBackupPath, CancellationToken cancellationToken = default) =>
        await RestoreAttachmentsAsync(
            attachmentBackupPath,
            beforeBackupCopy: null,
            cancellationToken);

    internal async Task RestoreAttachmentsAsync(
        string attachmentBackupPath,
        Action<string>? beforeBackupCopy,
        CancellationToken cancellationToken)
    {
        var entries = await LoadAndValidateAttachmentBackupAsync(attachmentBackupPath, cancellationToken);
        await RestoreAttachmentsAsync(
            attachmentBackupPath,
            entries,
            beforeBackupCopy,
            cancellationToken);
    }

    private async Task RestoreAttachmentsAsync(
        string attachmentBackupPath,
        IReadOnlyCollection<AttachmentBackupEntry> entries,
        Action<string>? beforeBackupCopy,
        CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = SafeCombine(_options.AttachmentRoot, entry.RelativePath);
            var backup = SafeCombine(attachmentBackupPath, entry.RelativePath);
            if (entry.Existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? restoreTemporary = null;
                Exception? attachmentRestoreException = null;
                try
                {
                    beforeBackupCopy?.Invoke(entry.RelativePath);
                    // 回调代表校验与实际复制之间可能发生的外部文件系统变化；使用前重新解析边界和链接。
                    target = SafeCombine(_options.AttachmentRoot, entry.RelativePath);
                    backup = SafeCombine(attachmentBackupPath, entry.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    restoreTemporary = SiblingWorkPath(target, "full-restore");
                    File.Copy(backup, restoreTemporary, overwrite: false);
                    if (!string.Equals(
                            await HashFileAsync(restoreTemporary, cancellationToken),
                            entry.Hash,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"附件恢复临时副本与清单不一致：{entry.RelativePath}");
                    target = SafeCombine(_options.AttachmentRoot, entry.RelativePath);
                    _ = SafeCombine(
                        _options.AttachmentRoot,
                        Path.GetRelativePath(Path.GetFullPath(_options.AttachmentRoot), restoreTemporary));
                    File.Move(restoreTemporary, target, overwrite: true);
                }
                catch (Exception exception)
                {
                    attachmentRestoreException = exception;
                    throw;
                }
                finally
                {
                    if (restoreTemporary is not null && File.Exists(restoreTemporary))
                    {
                        try
                        {
                            File.Delete(restoreTemporary);
                        }
                        catch (Exception cleanupException)
                        {
                            var cleanupState = new FollowUpAttachmentStateUncertainException(
                                $"完整附件恢复临时文件无法清理，必须人工处置：{entry.RelativePath}",
                                cleanupException);
                            if (attachmentRestoreException is not null)
                                throw new AggregateException(attachmentRestoreException, cleanupState);
                            throw cleanupState;
                        }
                    }
                }
            }
            else
            {
                target = SafeCombine(_options.AttachmentRoot, entry.RelativePath);
                if (Directory.Exists(target))
                    throw new InvalidDataException($"附件恢复目标存在目录项类型冲突：{entry.RelativePath}");
                if (File.Exists(target))
                    File.Delete(target);
                target = SafeCombine(_options.AttachmentRoot, entry.RelativePath);
                if (File.Exists(target) || Directory.Exists(target))
                    throw new InvalidDataException($"声明不存在的附件未能恢复为空路径：{entry.RelativePath}");
            }
        }
    }

    internal async Task<IReadOnlyList<string>> RestoreInstalledAttachmentsAsync(
        string attachmentBackupPath,
        IReadOnlyCollection<FollowUpAttachmentMutation> mutations,
        CancellationToken cancellationToken = default) =>
        await RestoreInstalledAttachmentsAsync(
            attachmentBackupPath,
            mutations,
            afterHashVerified: null,
            afterBackupTemporaryCopied: null,
            cancellationToken);

    internal async Task<IReadOnlyList<string>> RestoreInstalledAttachmentsAsync(
        string attachmentBackupPath,
        IReadOnlyCollection<FollowUpAttachmentMutation> mutations,
        Action<string>? afterHashVerified,
        CancellationToken cancellationToken) =>
        await RestoreInstalledAttachmentsAsync(
            attachmentBackupPath,
            mutations,
            afterHashVerified,
            afterBackupTemporaryCopied: null,
            cancellationToken);

    internal async Task<IReadOnlyList<string>> RestoreInstalledAttachmentsAsync(
        string attachmentBackupPath,
        IReadOnlyCollection<FollowUpAttachmentMutation> mutations,
        Action<string>? afterHashVerified,
        Action<string>? afterBackupTemporaryCopied,
        CancellationToken cancellationToken)
    {
        var entries = await LoadAttachmentBackupAsync(attachmentBackupPath, cancellationToken);
        return await RestoreInstalledAttachmentsAsync(
            attachmentBackupPath,
            entries,
            mutations,
            afterHashVerified,
            afterBackupTemporaryCopied,
            cancellationToken);
    }

    internal async Task<IReadOnlyList<string>> RestoreInstalledAttachmentsAsync(
        FollowUpAttachmentBackupSnapshot snapshot,
        IReadOnlyCollection<FollowUpAttachmentMutation> mutations,
        CancellationToken cancellationToken) =>
        await RestoreInstalledAttachmentsAsync(
            snapshot.AttachmentBackupPath,
            snapshot.Entries,
            mutations,
            afterHashVerified: null,
            afterBackupTemporaryCopied: null,
            cancellationToken);

    private async Task<IReadOnlyList<string>> RestoreInstalledAttachmentsAsync(
        string attachmentBackupPath,
        IReadOnlyList<AttachmentBackupEntry> entries,
        IReadOnlyCollection<FollowUpAttachmentMutation> mutations,
        Action<string>? afterHashVerified,
        Action<string>? afterBackupTemporaryCopied,
        CancellationToken cancellationToken)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var entriesByPath = entries.ToDictionary(entry => entry.RelativePath, comparer);
        var skipped = new List<string>();

        foreach (var mutation in mutations.DistinctBy(item => item.RelativePath, comparer))
        {
            if (!entriesByPath.TryGetValue(mutation.RelativePath, out var entry))
                throw new InvalidDataException($"附件备份清单缺少已安装路径：{mutation.RelativePath}");
            var target = SafeCombine(_options.AttachmentRoot, mutation.RelativePath);
            string? backup = null;
            string? restoreTemporary = null;
            Exception? compensationException = null;
            try
            {
                if (entry.Existed)
                {
                    backup = SafeCombine(attachmentBackupPath, entry.RelativePath);
                    await ValidateAttachmentBackupEntryAsync(entry, backup, cancellationToken);
                }
                target = RefreshControlledAttachmentPaths(mutation.RelativePath);
                if (!File.Exists(target))
                {
                    skipped.Add(mutation.RelativePath);
                    continue;
                }

                if (entry.Existed)
                {
                    target = RefreshControlledAttachmentPaths(mutation.RelativePath);
                    restoreTemporary = SiblingWorkPath(target, "restore");
                    _ = RefreshControlledAttachmentPaths(mutation.RelativePath, restoreTemporary);
                    File.Copy(backup!, restoreTemporary, overwrite: false);
                    afterBackupTemporaryCopied?.Invoke(restoreTemporary);
                    target = RefreshControlledAttachmentPaths(mutation.RelativePath, restoreTemporary);
                    if (!string.Equals(
                            await HashFileAsync(restoreTemporary, cancellationToken),
                            entry.Hash,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"附件补偿临时副本与清单不一致：{entry.RelativePath}");
                    target = RefreshControlledAttachmentPaths(mutation.RelativePath, restoreTemporary);
                    EnsureAtomicPublishSupported(restoreTemporary);
                }
                else
                {
                    target = RefreshControlledAttachmentPaths(mutation.RelativePath);
                    EnsureAtomicPublishSupported(target);
                }

                target = RefreshControlledAttachmentPaths(mutation.RelativePath, restoreTemporary);
                var claimed = SiblingClaimPath(target, "restore");
                _ = RefreshControlledAttachmentPaths(mutation.RelativePath, restoreTemporary, claimed);
                var targetClaimed = false;
                var claimedIsInstalledPackage = false;
                var canDiscardClaim = false;
                try
                {
                    try
                    {
                        File.Move(target, claimed, overwrite: false);
                    }
                    catch (IOException) when (!File.Exists(target))
                    {
                        skipped.Add(mutation.RelativePath);
                        continue;
                    }
                    targetClaimed = true;
                    target = RefreshControlledAttachmentPaths(mutation.RelativePath, restoreTemporary, claimed);

                    if (!string.Equals(
                            await HashFileAsync(claimed, cancellationToken),
                            mutation.InstalledHash,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        RestoreClaimedTarget(claimed, target, mutation.RelativePath);
                        claimed = string.Empty;
                        targetClaimed = false;
                        skipped.Add(mutation.RelativePath);
                        continue;
                    }

                    target = RefreshControlledAttachmentPaths(mutation.RelativePath, restoreTemporary, claimed);
                    claimedIsInstalledPackage = true;
                    afterHashVerified?.Invoke(mutation.RelativePath);
                    if (entry.Existed)
                    {
                        try
                        {
                            target = RefreshControlledAttachmentPaths(
                                mutation.RelativePath,
                                restoreTemporary,
                                claimed);
                            PublishWithoutOverwrite(restoreTemporary!, target);
                            canDiscardClaim = true;
                        }
                        catch (IOException) when (File.Exists(target))
                        {
                            skipped.Add(mutation.RelativePath);
                            canDiscardClaim = true;
                        }
                    }
                    else
                    {
                        target = RefreshControlledAttachmentPaths(mutation.RelativePath, claimed);
                        if (File.Exists(target))
                            skipped.Add(mutation.RelativePath);
                        canDiscardClaim = true;
                    }
                }
                catch (Exception restoreException) when (
                    targetClaimed
                    && !claimedIsInstalledPackage
                    && !RequiresFullRestore(restoreException))
                {
                    try
                    {
                        RestoreClaimedTarget(claimed, target, mutation.RelativePath);
                        claimed = string.Empty;
                        targetClaimed = false;
                    }
                    catch (Exception putBackException)
                    {
                        throw new AggregateException(restoreException, putBackException);
                    }
                    throw;
                }
                catch (Exception restoreException) when (
                    targetClaimed
                    && claimedIsInstalledPackage
                    && !canDiscardClaim
                    && !RequiresFullRestore(restoreException))
                {
                    throw new FollowUpAttachmentStateUncertainException(
                        $"附件补偿未完成，最后可用的包附件已保留在认领文件中，必须人工处置：{mutation.RelativePath}",
                        restoreException);
                }
                finally
                {
                    // 仅在旧版本已发布、外部新版本已占位或原路径本就不存在时，才可丢弃包附件认领文件。
                    if (canDiscardClaim && !string.IsNullOrEmpty(claimed) && File.Exists(claimed))
                    {
                        _ = RefreshControlledAttachmentPaths(mutation.RelativePath, claimed);
                        File.Delete(claimed);
                    }
                }
            }
            catch (Exception exception)
            {
                compensationException = exception;
                throw;
            }
            finally
            {
                if (restoreTemporary is not null && File.Exists(restoreTemporary))
                {
                    try
                    {
                        _ = RefreshControlledAttachmentPaths(mutation.RelativePath, restoreTemporary);
                        File.Delete(restoreTemporary);
                    }
                    catch (Exception cleanupException)
                    {
                        var cleanupState = new FollowUpAttachmentStateUncertainException(
                            $"附件补偿临时文件无法清理，必须人工处置：{mutation.RelativePath}",
                            cleanupException);
                        if (compensationException is not null)
                            throw new AggregateException(compensationException, cleanupState);
                        throw cleanupState;
                    }
                }
            }
        }

        return skipped;
    }

    internal async Task<IReadOnlyList<FollowUpAttachmentMutation>> InstallAttachmentsAsync(
        FollowUpVerifiedPackage package,
        string attachmentBackupPath,
        CancellationToken cancellationToken = default) =>
        await InstallAttachmentsAsync(
            package,
            attachmentBackupPath,
            afterTargetClaimed: null,
            beforeClaimCleanup: null,
            beforeTemporaryCleanup: null,
            afterInstallTemporaryCopied: null,
            beforeAtomicProbeCleanup: null,
            cancellationToken);

    internal async Task<IReadOnlyList<FollowUpAttachmentMutation>> InstallAttachmentsAsync(
        FollowUpVerifiedPackage package,
        FollowUpAttachmentBackupSnapshot snapshot,
        CancellationToken cancellationToken) =>
        await InstallAttachmentsAsync(
            package,
            snapshot.AttachmentBackupPath,
            snapshot.Entries,
            afterTargetClaimed: null,
            beforeClaimCleanup: null,
            beforeTemporaryCleanup: null,
            afterInstallTemporaryCopied: null,
            beforeAtomicProbeCleanup: null,
            cancellationToken);

    internal async Task<IReadOnlyList<FollowUpAttachmentMutation>> InstallAttachmentsAsync(
        FollowUpVerifiedPackage package,
        string attachmentBackupPath,
        Action<string>? afterTargetClaimed,
        CancellationToken cancellationToken) =>
        await InstallAttachmentsAsync(
            package,
            attachmentBackupPath,
            afterTargetClaimed,
            beforeClaimCleanup: null,
            beforeTemporaryCleanup: null,
            afterInstallTemporaryCopied: null,
            cancellationToken);

    internal async Task<IReadOnlyList<FollowUpAttachmentMutation>> InstallAttachmentsAsync(
        FollowUpVerifiedPackage package,
        string attachmentBackupPath,
        Action<string>? afterTargetClaimed,
        Action<string>? beforeClaimCleanup,
        CancellationToken cancellationToken) =>
        await InstallAttachmentsAsync(
            package,
            attachmentBackupPath,
            afterTargetClaimed,
            beforeClaimCleanup,
            beforeTemporaryCleanup: null,
            afterInstallTemporaryCopied: null,
            cancellationToken);

    internal async Task<IReadOnlyList<FollowUpAttachmentMutation>> InstallAttachmentsAsync(
        FollowUpVerifiedPackage package,
        string attachmentBackupPath,
        Action<string>? afterTargetClaimed,
        Action<string>? beforeClaimCleanup,
        Action<string>? beforeTemporaryCleanup,
        CancellationToken cancellationToken) =>
        await InstallAttachmentsAsync(
            package,
            attachmentBackupPath,
            afterTargetClaimed,
            beforeClaimCleanup,
            beforeTemporaryCleanup,
            afterInstallTemporaryCopied: null,
            cancellationToken);

    internal async Task<IReadOnlyList<FollowUpAttachmentMutation>> InstallAttachmentsAsync(
        FollowUpVerifiedPackage package,
        string attachmentBackupPath,
        Action<string>? afterTargetClaimed,
        Action<string>? beforeClaimCleanup,
        Action<string>? beforeTemporaryCleanup,
        Action<string>? afterInstallTemporaryCopied,
        CancellationToken cancellationToken) =>
        await InstallAttachmentsAsync(
            package,
            attachmentBackupPath,
            afterTargetClaimed,
            beforeClaimCleanup,
            beforeTemporaryCleanup,
            afterInstallTemporaryCopied,
            beforeAtomicProbeCleanup: null,
            cancellationToken);

    internal async Task<IReadOnlyList<FollowUpAttachmentMutation>> InstallAttachmentsAsync(
        FollowUpVerifiedPackage package,
        string attachmentBackupPath,
        Action<string>? afterTargetClaimed,
        Action<string>? beforeClaimCleanup,
        Action<string>? beforeTemporaryCleanup,
        Action<string>? afterInstallTemporaryCopied,
        Action<string>? beforeAtomicProbeCleanup,
        CancellationToken cancellationToken)
    {
        var entries = await LoadAttachmentBackupAsync(attachmentBackupPath, cancellationToken);
        return await InstallAttachmentsAsync(
            package,
            attachmentBackupPath,
            entries,
            afterTargetClaimed,
            beforeClaimCleanup,
            beforeTemporaryCleanup,
            afterInstallTemporaryCopied,
            beforeAtomicProbeCleanup,
            cancellationToken);
    }

    private async Task<IReadOnlyList<FollowUpAttachmentMutation>> InstallAttachmentsAsync(
        FollowUpVerifiedPackage package,
        string attachmentBackupPath,
        IReadOnlyList<AttachmentBackupEntry> entries,
        Action<string>? afterTargetClaimed,
        Action<string>? beforeClaimCleanup,
        Action<string>? beforeTemporaryCleanup,
        Action<string>? afterInstallTemporaryCopied,
        Action<string>? beforeAtomicProbeCleanup,
        CancellationToken cancellationToken)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var entriesByPath = entries.ToDictionary(entry => entry.RelativePath, comparer);
        var mutations = new List<FollowUpAttachmentMutation>();
        try
        {
            foreach (var attachment in package.Manifest.AttachmentFiles)
            {
                var relative = NormalizeAttachmentPath(attachment.Path);
                if (!entriesByPath.TryGetValue(relative, out var entry))
                    throw new InvalidDataException($"附件备份清单缺少包内路径：{relative}");
                var source = SafeCombine(package.StagingPath, attachment.Path.Replace('/', Path.DirectorySeparatorChar));
                var target = SafeCombine(_options.AttachmentRoot, relative);
                if (!File.Exists(source)) throw new FileNotFoundException("包内附件不存在。", source);

                if (entry.Existed)
                {
                    if (string.IsNullOrWhiteSpace(entry.Hash))
                        throw new InvalidDataException($"附件备份清单缺少原始 hash：{relative}");
                    var backup = SafeCombine(attachmentBackupPath, entry.RelativePath);
                    if (!File.Exists(backup)
                        || !string.Equals(
                            await HashFileAsync(backup, cancellationToken),
                            entry.Hash,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"附件备份内容与清单不一致：{relative}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                target = RefreshControlledAttachmentPaths(relative);
                var temporary = SiblingWorkPath(target, "install");
                _ = RefreshControlledAttachmentPaths(relative, temporary);
                string? claimed = null;
                var targetClaimed = false;
                Exception? attachmentOperationException = null;
                try
                {
                    try
                    {
                        File.Copy(source, temporary, overwrite: false);
                        afterInstallTemporaryCopied?.Invoke(temporary);
                        target = RefreshControlledAttachmentPaths(relative, temporary);
                        if (new FileInfo(temporary).Length != attachment.SizeBytes
                            || !string.Equals(
                                await HashFileAsync(temporary, cancellationToken),
                                attachment.Hash,
                                StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"附件校验失败：{relative}");
                        target = RefreshControlledAttachmentPaths(relative, temporary);
                        EnsureAtomicPublishSupported(temporary, beforeAtomicProbeCleanup);
                        if (entry.Existed)
                        {
                            target = RefreshControlledAttachmentPaths(relative, temporary);
                            if (!File.Exists(target))
                                throw new InvalidOperationException($"附件在备份后被删除，拒绝覆盖：{relative}");
                            claimed = SiblingClaimPath(target, "install");
                            _ = RefreshControlledAttachmentPaths(relative, temporary, claimed);
                            File.Move(target, claimed, overwrite: false);
                            targetClaimed = true;
                            target = RefreshControlledAttachmentPaths(relative, temporary, claimed);
                            if (!string.Equals(
                                    await HashFileAsync(claimed, cancellationToken),
                                    entry.Hash,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                var conflict = new InvalidOperationException($"附件在备份后已被更新，拒绝覆盖：{relative}");
                                try
                                {
                                    RestoreClaimedTarget(claimed, target, relative);
                                    claimed = null;
                                    targetClaimed = false;
                                }
                                catch (Exception restoreException)
                                {
                                    throw new AggregateException(conflict, restoreException);
                                }
                                throw conflict;
                            }
                        }

                        afterTargetClaimed?.Invoke(relative);
                        target = RefreshControlledAttachmentPaths(relative, temporary, claimed);
                        PublishWithoutOverwrite(temporary, target);

                        mutations.Add(new FollowUpAttachmentMutation(relative, attachment.Hash));
                        if (claimed is not null)
                        {
                            try
                            {
                                beforeClaimCleanup?.Invoke(claimed);
                                _ = RefreshControlledAttachmentPaths(relative, claimed);
                                File.Delete(claimed);
                            }
                            catch (Exception cleanupException) when (!RequiresFullRestore(cleanupException))
                            {
                                throw new FollowUpAttachmentStateUncertainException(
                                    $"包附件已发布，但原版本认领文件无法清理，必须人工处置：{relative}",
                                    cleanupException);
                            }
                            claimed = null;
                            targetClaimed = false;
                        }
                    }
                    catch (Exception installException) when (
                        targetClaimed
                        && claimed is not null
                        && !RequiresFullRestore(installException))
                    {
                        try
                        {
                            target = RefreshControlledAttachmentPaths(relative, claimed);
                            if (File.Exists(target))
                            {
                                _ = RefreshControlledAttachmentPaths(relative, claimed);
                                File.Delete(claimed);
                            }
                            else
                                RestoreClaimedTarget(claimed, target, relative);
                            claimed = null;
                            targetClaimed = false;
                        }
                        catch (Exception restoreException)
                        {
                            throw new AggregateException(installException, restoreException);
                        }
                        throw;
                    }
                }
                catch (Exception exception)
                {
                    attachmentOperationException = exception;
                    throw;
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        try
                        {
                            beforeTemporaryCleanup?.Invoke(temporary);
                            _ = RefreshControlledAttachmentPaths(relative, temporary);
                            File.Delete(temporary);
                        }
                        catch (Exception cleanupException)
                        {
                            var cleanupState = new FollowUpAttachmentStateUncertainException(
                                $"附件安装临时文件无法清理，必须人工处置：{relative}",
                                cleanupException);
                            if (attachmentOperationException is not null)
                                throw new AggregateException(attachmentOperationException, cleanupState);
                            throw cleanupState;
                        }
                    }
                }
            }

            return mutations;
        }
        catch (Exception exception) when (mutations.Count > 0 || RequiresFullRestore(exception))
        {
            throw new FollowUpAttachmentInstallException(
                mutations.ToArray(),
                exception,
                RequiresFullRestore(exception));
        }
    }

    private async Task RunPostgreSqlToolAsync(string tool, string filePath, bool restore, CancellationToken cancellationToken)
    {
        var executable = FindExecutable(tool) ?? throw new InvalidOperationException($"未找到 {tool}，请安装 PostgreSQL client。");
        var builder = new NpgsqlConnectionStringBuilder(_cubeConnectionString);
        var hostValue = builder.Host ?? throw new InvalidOperationException("CubeDb 未配置 Host。");
        var user = builder.Username ?? throw new InvalidOperationException("CubeDb 未配置 Username。");
        var database = builder.Database ?? throw new InvalidOperationException("CubeDb 未配置 Database。");
        var (host, port) = SplitHost(hostValue, builder.Port);
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["PGPASSWORD"] = builder.Password;
        Add(startInfo, "-h", host, "-p", port.ToString(), "-U", user);
        if (restore)
        {
            Add(startInfo, "--clean", "--if-exists", "--no-owner", "--no-privileges", "--exit-on-error", "-d", database, filePath);
        }
        else
        {
            Add(startInfo, "-Fc", "--no-owner", "--no-privileges", "-d", database, "-f", filePath);
        }
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"无法启动 {tool}。");
        var errorTask = process.StandardError.ReadToEndAsync();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        await WaitForPostgreSqlToolExitAsync(process, cancellationToken);
        var error = await errorTask;
        _ = await outputTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{tool} 执行失败：{Truncate(error, 1000)}");
    }

    internal static async Task WaitForPostgreSqlToolExitAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            // Kill 只发出终止请求；必须确认进程树实际退出后，调用方才能释放维护租约。
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }

    internal static string NormalizeAttachmentPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        const string prefix = "files/uploads/";
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal) || normalized[prefix.Length..].Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("附件路径不符合 files/uploads 契约。");
        var relative = normalized[prefix.Length..];
        return relative.Replace('/', Path.DirectorySeparatorChar);
    }

    internal static string GetAttachmentBackupManifestPath(string attachmentBackupPath)
    {
        var externalPath = GetExternalAttachmentBackupManifestPath(attachmentBackupPath);
        return File.Exists(externalPath)
            ? externalPath
            : Path.Combine(Path.GetFullPath(attachmentBackupPath), AttachmentBackupManifestFileName);
    }

    internal static string GetRegisteredAttachmentBackupManifestPath(
        string attachmentBackupPath,
        bool requireExternalManifest) =>
        requireExternalManifest
            ? GetExternalAttachmentBackupManifestPath(attachmentBackupPath)
            : GetAttachmentBackupManifestPath(attachmentBackupPath);

    private static string GetExternalAttachmentBackupManifestPath(string attachmentBackupPath) =>
        GetBackupMetadataPath(attachmentBackupPath, AttachmentBackupManifestFileName);

    private static string GetBackupMetadataPath(string attachmentBackupPath, string fileName)
    {
        var fullAttachmentPath = Path.GetFullPath(attachmentBackupPath);
        var root = Directory.GetParent(fullAttachmentPath)?.FullName
                   ?? throw new InvalidDataException("附件备份目录缺少父目录。");
        return Path.Combine(root, fileName);
    }

    private static string GetBackupMetadataStagingRoot(string attachmentBackupPath) =>
        GetBackupMetadataPath(
            attachmentBackupPath,
            AttachmentBackupMetadataStagingDirectoryName);

    private static string CreateMetadataStagingPath(
        string attachmentBackupPath,
        string operation)
    {
        var metadataRoot = Directory.GetParent(Path.GetFullPath(attachmentBackupPath))?.FullName
                           ?? throw new InvalidDataException("附件备份目录缺少父目录。");
        var stagingRoot = GetBackupMetadataStagingRoot(attachmentBackupPath);
        EnsurePathHasNoLinks(metadataRoot, stagingRoot);
        Directory.CreateDirectory(stagingRoot);
        EnsurePathHasNoLinks(metadataRoot, stagingRoot);
        RestrictDirectory(stagingRoot);
        return Path.Combine(stagingRoot, $"{Guid.NewGuid():N}.{operation}.tmp");
    }

    private void ValidateArtifactLayout(FollowUpBackupArtifact artifact)
    {
        var managedRoot = Path.GetFullPath(_options.BackupRoot);
        var artifactRoot = EnsureManagedPath(managedRoot, artifact.RootPath, allowRoot: true);
        EnsurePathHasNoLinks(managedRoot, artifactRoot);
        var databasePath = EnsureManagedPath(artifactRoot, artifact.DatabaseBackupPath, allowRoot: false);
        var attachmentPath = EnsureManagedPath(artifactRoot, artifact.AttachmentBackupPath, allowRoot: false);
        EnsurePathHasNoLinks(artifactRoot, databasePath);
        EnsurePathHasNoLinks(artifactRoot, attachmentPath);
    }

    private static string EnsureManagedPath(string root, string candidate, bool allowRoot)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (allowRoot && fullCandidate.Equals(fullRoot, comparison))
            return fullCandidate;
        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!fullCandidate.StartsWith(prefix, comparison))
            throw new InvalidDataException($"备份路径逃逸允许目录：{fullCandidate}");
        return fullCandidate;
    }

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("文件路径逃逸允许目录。");
        EnsurePathHasNoLinks(fullRoot, target);
        return target;
    }

    private string RefreshControlledAttachmentPaths(
        string relativePath,
        params string?[] siblingPaths)
    {
        var attachmentRoot = Path.GetFullPath(_options.AttachmentRoot);
        var target = SafeCombine(attachmentRoot, relativePath);
        var targetDirectory = Path.GetDirectoryName(target)
                              ?? throw new InvalidDataException("附件目标路径缺少父目录。");
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (var siblingPath in siblingPaths)
        {
            if (string.IsNullOrWhiteSpace(siblingPath))
                continue;
            var fullSiblingPath = Path.GetFullPath(siblingPath);
            var refreshedSiblingPath = SafeCombine(
                attachmentRoot,
                Path.GetRelativePath(attachmentRoot, fullSiblingPath));
            if (!fullSiblingPath.Equals(refreshedSiblingPath, comparison)
                || !string.Equals(
                    Path.GetDirectoryName(refreshedSiblingPath),
                    targetDirectory,
                    comparison))
                throw new InvalidDataException($"附件临时或认领路径已离开目标受控目录：{relativePath}");
        }
        return target;
    }

    private static void EnsurePathHasNoLinks(string fullRoot, string target)
    {
        EnsureNotLink(fullRoot);
        var current = fullRoot;
        foreach (var segment in Path.GetRelativePath(fullRoot, target)
                     .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureNotLink(current);
        }
    }

    private static void EnsureNotLink(string path)
    {
        FileSystemInfo[] candidates = [new DirectoryInfo(path), new FileInfo(path)];
        foreach (var candidate in candidates)
        {
            try
            {
                candidate.Refresh();
                if (candidate.LinkTarget is not null
                    || candidate.Exists && candidate.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException($"备份或附件路径包含符号链接或重解析点：{path}");
            }
            catch (FileNotFoundException)
            {
                // 尚未创建的目标组件由调用方在已校验父目录下创建。
            }
            catch (DirectoryNotFoundException)
            {
                // 尚未创建的目标组件由调用方在已校验父目录下创建。
            }
        }
    }

    private static string SiblingWorkPath(string target, string operation) =>
        $"{target}.{Guid.NewGuid():N}.{operation}.tmp";

    private static string SiblingClaimPath(string target, string operation) =>
        $"{target}.{Guid.NewGuid():N}.{operation}.claim";

    private async Task<AttachmentBackupRegistration> ResolveAttachmentBackupRegistrationAsync(
        FollowUpBackupArtifact artifact,
        byte[] manifestBytes,
        CancellationToken cancellationToken)
    {
        var hasManifestHash = !string.IsNullOrWhiteSpace(artifact.AttachmentManifestHash);
        var hasEntryCount = artifact.AttachmentEntryCount.HasValue;
        if (hasManifestHash != hasEntryCount)
            throw new InvalidDataException("附件备份登记的清单 hash 与条目数不完整。");
        if (hasManifestHash)
            return new AttachmentBackupRegistration(
                artifact.AttachmentManifestHash!,
                artifact.AttachmentEntryCount!.Value,
                RequiresReview: false,
                CandidateAnchor: null);

        var anchorPath = GetBackupMetadataPath(
            artifact.AttachmentBackupPath,
            LegacyArtifactAnchorFileName);
        EnsurePathHasNoLinks(Path.GetFullPath(artifact.RootPath), anchorPath);
        if (File.Exists(anchorPath))
        {
            var anchor = JsonSerializer.Deserialize<LegacyArtifactAnchor>(
                             await File.ReadAllBytesAsync(anchorPath, cancellationToken),
                             FollowUpJson.Options)
                         ?? throw new InvalidDataException("旧备份附件清单信任锚点格式无效。");
            if (anchor.Version != AttachmentBackupManifestVersion
                || anchor.RegisteredSizeBytes != artifact.SizeBytes
                || anchor.EntryCount < 0
                || string.IsNullOrWhiteSpace(anchor.ManifestHash))
                throw new InvalidDataException("旧备份附件清单信任锚点与备份登记不一致。");
            return new AttachmentBackupRegistration(
                anchor.ManifestHash,
                anchor.EntryCount,
                RequiresReview: false,
                CandidateAnchor: null);
        }

        var anchorToReview = new LegacyArtifactAnchor(
            AttachmentBackupManifestVersion,
            HashBytes(manifestBytes),
            ReadManifestEntryCount(manifestBytes),
            artifact.SizeBytes);
        return new AttachmentBackupRegistration(
            anchorToReview.ManifestHash,
            anchorToReview.EntryCount,
            RequiresReview: true,
            CandidateAnchor: anchorToReview);
    }

    private static async Task PublishLegacyArtifactAnchorAsync(
        FollowUpBackupArtifact artifact,
        LegacyArtifactAnchor anchor,
        CancellationToken cancellationToken)
    {
        var anchorPath = GetBackupMetadataPath(
            artifact.AttachmentBackupPath,
            LegacyArtifactAnchorFileName);
        EnsurePathHasNoLinks(Path.GetFullPath(artifact.RootPath), anchorPath);
        var temporary = CreateMetadataStagingPath(
            artifact.AttachmentBackupPath,
            "artifact-anchor");
        Exception? publishException = null;
        try
        {
            await File.WriteAllBytesAsync(
                temporary,
                JsonSerializer.SerializeToUtf8Bytes(anchor, FollowUpJson.Options),
                cancellationToken);
            try
            {
                PublishWithoutOverwrite(temporary, anchorPath);
            }
            catch (IOException) when (File.Exists(anchorPath))
            {
                // 另一恢复实例已生成锚点；本次仍保持只预检不恢复，重试时再读取并验证。
            }
        }
        catch (Exception exception)
        {
            publishException = exception;
            throw;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception cleanupException)
                {
                    var cleanupFailure = new IOException(
                        "旧备份附件清单信任锚点临时文件无法清理，必须人工处置。",
                        cleanupException);
                    if (publishException is not null)
                        throw new AggregateException(publishException, cleanupFailure);
                    throw cleanupFailure;
                }
            }
        }
    }

    private static int ReadManifestEntryCount(byte[] manifestBytes)
    {
        using var document = JsonDocument.Parse(manifestBytes);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
            return root.GetArrayLength();
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("entries", out var entries)
            && entries.ValueKind == JsonValueKind.Array)
            return entries.GetArrayLength();
        throw new InvalidDataException("附件备份清单缺少有效 entries。");
    }

    private static long ComputeRegisteredBackupSize(FollowUpBackupArtifact artifact)
    {
        if (!Directory.Exists(artifact.RootPath))
            throw new DirectoryNotFoundException($"备份根目录不存在：{artifact.RootPath}");
        var legacyBaselinePath = Path.GetFullPath(GetBackupMetadataPath(
            artifact.AttachmentBackupPath,
            LegacyAttachmentHashBaselineFileName));
        var artifactAnchorPath = Path.GetFullPath(GetBackupMetadataPath(
            artifact.AttachmentBackupPath,
            LegacyArtifactAnchorFileName));
        var metadataStagingRoot = Path.GetFullPath(GetBackupMetadataStagingRoot(
            artifact.AttachmentBackupPath));
        EnsurePathHasNoLinks(Path.GetFullPath(artifact.RootPath), metadataStagingRoot);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return Directory.EnumerateFiles(artifact.RootPath, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var fullPath = Path.GetFullPath(path);
                return !fullPath.Equals(legacyBaselinePath, pathComparison)
                       && !fullPath.Equals(artifactAnchorPath, pathComparison)
                       && !IsPathWithin(metadataStagingRoot, fullPath, pathComparison);
            })
            .Sum(path => new FileInfo(path).Length);
    }

    private static bool IsPathWithin(string root, string candidate, StringComparison comparison)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison);
    }

    private void EnsureSnapshotPathOutsideBackupRoot(string path, string errorMessage)
    {
        var backupRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.BackupRoot));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        EnsurePathHasNoLinks(
            Path.GetPathRoot(backupRoot)
            ?? throw new InvalidOperationException("无法确定 BackupRoot 所在文件系统根目录。"),
            backupRoot);
        EnsurePathHasNoLinks(
            Path.GetPathRoot(candidate)
            ?? throw new InvalidOperationException("无法确定临时快照所在文件系统根目录。"),
            candidate);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (candidate.Equals(backupRoot, comparison)
            || IsPathWithin(backupRoot, candidate, comparison))
            throw new InvalidOperationException(errorMessage);
    }

    private void RestoreClaimedTarget(string claimed, string target, string relativePath)
    {
        try
        {
            target = RefreshControlledAttachmentPaths(relativePath, claimed);
            PublishWithoutOverwrite(claimed, target);
            _ = RefreshControlledAttachmentPaths(relativePath, claimed);
            File.Delete(claimed);
        }
        catch (Exception exception)
        {
            throw new FollowUpAttachmentStateUncertainException(
                $"附件并发版本无法放回原路径，已保留认领文件，必须人工处置：{relativePath}",
                exception);
        }
    }

    private static bool RequiresFullRestore(Exception exception) =>
        exception is FollowUpAttachmentStateUncertainException
        || exception is AggregateException aggregate && aggregate.InnerExceptions.Any(RequiresFullRestore)
        || exception.InnerException is not null && RequiresFullRestore(exception.InnerException);

    private async Task<IReadOnlyList<AttachmentBackupEntry>> LoadAndValidateAttachmentBackupAsync(
        string attachmentBackupPath,
        CancellationToken cancellationToken)
    {
        var manifestPath = GetAttachmentBackupManifestPath(attachmentBackupPath);
        EnsureNotLink(manifestPath);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("附件备份清单不存在。", manifestPath);
        return await LoadAndValidateAttachmentBackupAsync(
            attachmentBackupPath,
            await File.ReadAllBytesAsync(manifestPath, cancellationToken),
            cancellationToken);
    }

    private async Task<IReadOnlyList<AttachmentBackupEntry>> LoadAndValidateAttachmentBackupAsync(
        string attachmentBackupPath,
        byte[] manifestBytes,
        CancellationToken cancellationToken)
    {
        var entries = await LoadAttachmentBackupAsync(
            attachmentBackupPath,
            manifestBytes,
            cancellationToken);
        foreach (var entry in entries.Where(entry => entry.Existed))
        {
            var backup = SafeCombine(attachmentBackupPath, entry.RelativePath);
            await ValidateAttachmentBackupEntryAsync(entry, backup, cancellationToken);
        }
        return entries;
    }

    private async Task<IReadOnlyList<AttachmentBackupEntry>> LoadAttachmentBackupAsync(
        string attachmentBackupPath,
        CancellationToken cancellationToken)
    {
        var manifestPath = GetAttachmentBackupManifestPath(attachmentBackupPath);
        EnsureNotLink(manifestPath);
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("附件备份清单不存在。", manifestPath);
        return await LoadAttachmentBackupAsync(
            attachmentBackupPath,
            await File.ReadAllBytesAsync(manifestPath, cancellationToken),
            cancellationToken);
    }

    private async Task<IReadOnlyList<AttachmentBackupEntry>> LoadAttachmentBackupAsync(
        string attachmentBackupPath,
        byte[] manifestBytes,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(manifestBytes);
        var legacyFormat = document.RootElement.ValueKind == JsonValueKind.Array;
        List<AttachmentBackupEntry> entries;
        if (legacyFormat)
        {
            entries = JsonSerializer.Deserialize<List<AttachmentBackupEntry>>(
                          manifestBytes, FollowUpJson.Options) ?? [];
        }
        else
        {
            var manifest = JsonSerializer.Deserialize<AttachmentBackupManifest>(
                               manifestBytes, FollowUpJson.Options)
                           ?? throw new InvalidDataException("附件备份清单格式无效。");
            if (manifest.Version != AttachmentBackupManifestVersion)
                throw new InvalidDataException($"不支持的附件备份清单版本：{manifest.Version}。");
            entries = manifest.Entries
                      ?? throw new InvalidDataException("附件备份清单缺少 entries。");
        }

        ValidateAttachmentBackupEntries(attachmentBackupPath, entries);
        if (legacyFormat && entries.Any(entry => entry.Existed && string.IsNullOrWhiteSpace(entry.Hash)))
            entries = await LoadOrCreateLegacyHashBaselineAsync(
                attachmentBackupPath,
                entries,
                cancellationToken);
        return entries;
    }

    private void ValidateAttachmentBackupEntries(
        string attachmentBackupPath,
        IReadOnlyCollection<AttachmentBackupEntry> entries)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.RelativePath))
                throw new InvalidDataException("附件备份清单包含空路径。");
            _ = SafeCombine(_options.AttachmentRoot, entry.RelativePath);
            _ = SafeCombine(attachmentBackupPath, entry.RelativePath);
            if (!seen.Add(entry.RelativePath))
                throw new InvalidDataException($"附件备份清单包含重复路径：{entry.RelativePath}");
            if (!entry.Existed && !string.IsNullOrWhiteSpace(entry.Hash))
                throw new InvalidDataException($"不存在的附件不应记录 hash：{entry.RelativePath}");
        }
    }

    private async Task<List<AttachmentBackupEntry>> LoadOrCreateLegacyHashBaselineAsync(
        string attachmentBackupPath,
        IReadOnlyCollection<AttachmentBackupEntry> legacyEntries,
        CancellationToken cancellationToken)
    {
        var baselinePath = GetBackupMetadataPath(
            attachmentBackupPath,
            LegacyAttachmentHashBaselineFileName);
        EnsureNotLink(baselinePath);
        if (File.Exists(baselinePath))
        {
            var baseline = JsonSerializer.Deserialize<AttachmentBackupManifest>(
                               await File.ReadAllTextAsync(baselinePath, cancellationToken),
                               FollowUpJson.Options)
                           ?? throw new InvalidDataException("旧版附件备份 hash 基线格式无效。");
            if (baseline.Version != AttachmentBackupManifestVersion)
                throw new InvalidDataException($"不支持的附件备份 hash 基线版本：{baseline.Version}。");
            var baselineEntries = baseline.Entries
                                  ?? throw new InvalidDataException("旧版附件备份 hash 基线缺少 entries。");
            ValidateAttachmentBackupEntries(attachmentBackupPath, baselineEntries);
            ValidateLegacyBaselineMatches(legacyEntries, baselineEntries);
            return baselineEntries;
        }

        var upgradedEntries = new List<AttachmentBackupEntry>(legacyEntries.Count);
        foreach (var entry in legacyEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.Existed)
            {
                upgradedEntries.Add(entry with { Hash = null });
                continue;
            }

            var backup = SafeCombine(attachmentBackupPath, entry.RelativePath);
            if (!File.Exists(backup)) throw new FileNotFoundException("附件备份文件缺失。", backup);
            upgradedEntries.Add(entry with { Hash = await HashFileAsync(backup, cancellationToken) });
        }

        var baselineManifest = new AttachmentBackupManifest(
            AttachmentBackupManifestVersion,
            upgradedEntries);
        var temporary = CreateMetadataStagingPath(
            attachmentBackupPath,
            "legacy-baseline");
        Exception? publishException = null;
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(baselineManifest, FollowUpJson.Options),
                cancellationToken);
            try
            {
                PublishWithoutOverwrite(temporary, baselinePath);
            }
            catch (IOException) when (File.Exists(baselinePath))
            {
                // 另一恢复实例已生成基线；本次仍保持只预检不恢复，重试时再验证该基线。
            }
        }
        catch (Exception exception)
        {
            publishException = exception;
            throw;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception cleanupException)
                {
                    var cleanupFailure = new IOException(
                        "旧版附件备份 hash 基线临时文件无法清理，必须人工处置。",
                        cleanupException);
                    if (publishException is not null)
                        throw new AggregateException(publishException, cleanupFailure);
                    throw cleanupFailure;
                }
            }
        }

        throw BackupReviewRequired(
            $"检测到旧版无 hash 附件备份清单，已生成 {LegacyAttachmentHashBaselineFileName}；本次未执行恢复。请人工核对基线后再次执行恢复。",
            innerException: null);
    }

    private static void ValidateLegacyBaselineMatches(
        IReadOnlyCollection<AttachmentBackupEntry> legacyEntries,
        IReadOnlyCollection<AttachmentBackupEntry> baselineEntries)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var baselineByPath = baselineEntries.ToDictionary(entry => entry.RelativePath, comparer);
        if (baselineByPath.Count != legacyEntries.Count)
            throw new InvalidDataException("旧版附件备份 hash 基线与原清单路径集合不一致。");
        foreach (var legacyEntry in legacyEntries)
        {
            if (!baselineByPath.TryGetValue(legacyEntry.RelativePath, out var baselineEntry)
                || baselineEntry.Existed != legacyEntry.Existed
                || (!string.IsNullOrWhiteSpace(legacyEntry.Hash)
                    && !string.Equals(legacyEntry.Hash, baselineEntry.Hash, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"旧版附件备份 hash 基线与原清单不一致：{legacyEntry.RelativePath}");
        }
    }

    private static async Task ValidateAttachmentBackupEntryAsync(
        AttachmentBackupEntry entry,
        string backup,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(backup)) throw new FileNotFoundException("附件备份文件缺失。", backup);
        if (string.IsNullOrWhiteSpace(entry.Hash)
            || !string.Equals(
                await HashFileAsync(backup, cancellationToken),
                entry.Hash,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"附件备份内容与清单不一致：{entry.RelativePath}");
    }

    // File.Move(..., overwrite: false) 在 Unix 上可能退化为“先检查、再 rename”，目标在间隙出现时仍会被覆盖。
    // 同目录硬链接由文件系统以 create-if-absent 原子发布，成功后再由调用方删除源链接。
    private static void PublishWithoutOverwrite(string source, string target)
    {
        var success = OperatingSystem.IsWindows()
            ? CreateHardLinkWindows(target, source, IntPtr.Zero)
            : CreateHardLinkUnix(source, target) == 0;
        if (success)
            return;

        var error = Marshal.GetLastPInvokeError();
        throw new IOException(
            $"无法在不覆盖现有版本的前提下发布附件：{Path.GetFileName(target)}",
            new Win32Exception(error));
    }

    private static void EnsureAtomicPublishSupported(
        string source,
        Action<string>? beforeProbeCleanup = null)
    {
        var probe = SiblingWorkPath(source, "link-probe");
        Exception? probeException = null;
        try
        {
            PublishWithoutOverwrite(source, probe);
        }
        catch (Exception exception)
        {
            probeException = exception;
            throw;
        }
        finally
        {
            if (File.Exists(probe))
            {
                try
                {
                    beforeProbeCleanup?.Invoke(probe);
                    File.Delete(probe);
                }
                catch (Exception cleanupException)
                {
                    var cleanupState = new FollowUpAttachmentStateUncertainException(
                        $"附件硬链接能力探针无法清理，已保留残留路径并必须人工处置：{probe}",
                        cleanupException);
                    if (probeException is not null)
                        throw new AggregateException(probeException, cleanupState);
                    throw cleanupState;
                }
            }
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existingFileName, string fileName);

    private static (string Host, int Port) SplitHost(string host, int port)
    {
        var index = host.LastIndexOf(':');
        return index > 0 && int.TryParse(host[(index + 1)..], out var embeddedPort)
            ? (host[..index], embeddedPort)
            : (host, port);
    }

    private static string? FindExecutable(string name)
    {
        var executable = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(directory, executable);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static void Add(ProcessStartInfo info, params string[] arguments)
    {
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
    }
    private static void TryKill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch { } }
    private static string Truncate(string value, int max) => value[..Math.Min(value.Length, max)];
    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
    private static string HashBytes(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static InvalidDataException BackupReviewRequired(string message, Exception? innerException)
    {
        var exception = innerException is null
            ? new InvalidDataException(message)
            : new InvalidDataException(message, innerException);
        exception.Data[nameof(BackupReviewRequired)] = true;
        return exception;
    }
    private static bool IsBackupReviewRequired(Exception exception) =>
        exception.Data[nameof(BackupReviewRequired)] is true;
    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private sealed record AttachmentBackupManifest(int Version, List<AttachmentBackupEntry> Entries);
    internal sealed record AttachmentBackupEntry(string RelativePath, bool Existed, string? Hash = null);
    private sealed record AttachmentBackupRegistration(
        string ManifestHash,
        int EntryCount,
        bool RequiresReview,
        LegacyArtifactAnchor? CandidateAnchor);
    private sealed record LegacyArtifactAnchor(
        int Version,
        string ManifestHash,
        int EntryCount,
        long RegisteredSizeBytes);

    internal sealed class FollowUpAttachmentBackupSnapshot : IAsyncDisposable
    {
        private bool _disposed;

        internal FollowUpAttachmentBackupSnapshot(
            string attachmentBackupPath,
            string workRoot,
            IReadOnlyList<AttachmentBackupEntry> entries)
        {
            AttachmentBackupPath = attachmentBackupPath;
            WorkRoot = workRoot;
            Entries = entries;
        }

        public string AttachmentBackupPath { get; }
        public string WorkRoot { get; }
        internal IReadOnlyList<AttachmentBackupEntry> Entries { get; }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            if (Directory.Exists(WorkRoot)) Directory.Delete(WorkRoot, recursive: true);
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed record FollowUpAttachmentMutation(string RelativePath, string InstalledHash);

internal sealed record FollowUpDatabaseRestoreSnapshot(string FilePath, string WorkRoot);

internal sealed class FollowUpRestoreCleanupException(IReadOnlyList<Exception> cleanupErrors)
    : IOException(
        "数据库和附件已恢复，但临时快照清理失败，必须人工清理残留；不得重复执行恢复。",
        new AggregateException(cleanupErrors))
{
    public IReadOnlyList<Exception> CleanupErrors { get; } = cleanupErrors.ToArray();
}

internal sealed class FollowUpAttachmentInstallException(
    IReadOnlyList<FollowUpAttachmentMutation> mutations,
    Exception innerException,
    bool requiresFullRestore)
    : Exception(innerException.Message, innerException)
{
    public IReadOnlyList<FollowUpAttachmentMutation> Mutations { get; } = mutations;
    public bool RequiresFullRestore { get; } = requiresFullRestore;
}

internal sealed class FollowUpAttachmentStateUncertainException(string message, Exception innerException)
    : IOException(message, innerException);
