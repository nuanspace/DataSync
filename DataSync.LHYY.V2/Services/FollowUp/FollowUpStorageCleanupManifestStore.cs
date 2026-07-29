using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataSync.LHYY.V2.Models.FollowUp;
using Microsoft.Extensions.Logging;

namespace DataSync.LHYY.V2.Services.FollowUp;

public sealed class FollowUpStorageCleanupManifestStore
{
    private readonly string _root;
    private readonly ILogger<FollowUpStorageCleanupManifestStore>? _logger;

    public FollowUpStorageCleanupManifestStore(string packageRoot)
        : this(packageRoot, logger: null)
    {
    }

    public FollowUpStorageCleanupManifestStore(
        string packageRoot,
        ILogger<FollowUpStorageCleanupManifestStore>? logger)
    {
        _root = Path.Combine(Path.GetFullPath(packageRoot), ".cleanup-operations");
        _logger = logger;
    }

    public string GetPath(string hospitalCode, string packageId)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{hospitalCode}\n{packageId}")))
            .ToLowerInvariant();
        return Path.Combine(_root, $"{key}.json");
    }

    public async Task WriteAsync(FollowUpStorageCleanupManifest manifest, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        manifest.UpdatedAtUtc = DateTime.UtcNow;
        var path = GetPath(manifest.HospitalCode, manifest.PackageId);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<FollowUpStorageCleanupManifest?> ReadAsync(
        string hospitalCode,
        string packageId,
        CancellationToken cancellationToken)
    {
        var path = GetPath(hospitalCode, packageId);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<FollowUpStorageCleanupManifest>(stream,
            cancellationToken: cancellationToken)
            ?? throw new InvalidDataException($"存储清理操作清单为空：{path}");
    }

    public async Task<IReadOnlyList<FollowUpStorageCleanupManifest>> ReadAllAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root)) return [];
        var manifests = new List<FollowUpStorageCleanupManifest>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                manifests.Add(await JsonSerializer.DeserializeAsync<FollowUpStorageCleanupManifest>(stream,
                    cancellationToken: cancellationToken)
                    ?? throw new InvalidDataException($"存储清理操作清单为空：{path}"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException
                                              or IOException
                                              or InvalidDataException
                                              or UnauthorizedAccessException)
            {
                _logger?.LogError(
                    exception,
                    "存储清理操作清单无法读取，已保留原文件供人工处置并继续扫描其他清单。Path={Path}",
                    path);
            }
        }
        return manifests;
    }

    public void Delete(string hospitalCode, string packageId)
    {
        var path = GetPath(hospitalCode, packageId);
        if (File.Exists(path)) File.Delete(path);
    }
}

public static class FollowUpStorageCleanupFileRecovery
{
    public static IReadOnlyList<string> Restore(IEnumerable<FollowUpStorageCleanupManifestItem> items)
    {
        var errors = new List<string>();
        foreach (var item in items.Reverse())
        {
            try
            {
                if (item.IsDirectory && Directory.Exists(item.QuarantinePath) && !Directory.Exists(item.OriginalPath))
                    Directory.Move(item.QuarantinePath, item.OriginalPath);
                else if (!item.IsDirectory && File.Exists(item.QuarantinePath) && !File.Exists(item.OriginalPath))
                    File.Move(item.QuarantinePath, item.OriginalPath);

                var restored = item.IsDirectory
                    ? Directory.Exists(item.OriginalPath) && !Directory.Exists(item.QuarantinePath)
                    : File.Exists(item.OriginalPath) && !File.Exists(item.QuarantinePath);
                if (!restored) errors.Add(item.QuarantinePath);
            }
            catch
            {
                errors.Add(item.QuarantinePath);
            }
        }
        return errors;
    }

    public static IReadOnlyList<string> DeleteQuarantine(IEnumerable<FollowUpStorageCleanupManifestItem> items)
    {
        var errors = new List<string>();
        foreach (var item in items)
        {
            try
            {
                if (PathEntryExists(item.OriginalPath))
                {
                    errors.Add(item.OriginalPath);
                    continue;
                }

                if (item.IsDirectory && Directory.Exists(item.QuarantinePath))
                    Directory.Delete(item.QuarantinePath, recursive: true);
                else if (!item.IsDirectory && File.Exists(item.QuarantinePath))
                    File.Delete(item.QuarantinePath);

                if (PathEntryExists(item.OriginalPath))
                    errors.Add(item.OriginalPath);
                else if (PathEntryExists(item.QuarantinePath))
                    errors.Add(item.QuarantinePath);
            }
            catch
            {
                errors.Add(PathEntryExists(item.OriginalPath)
                    ? item.OriginalPath
                    : item.QuarantinePath);
            }
        }
        return errors;
    }

    private static bool PathEntryExists(string path)
    {
        FileSystemInfo[] candidates = [new DirectoryInfo(path), new FileInfo(path)];
        foreach (var candidate in candidates)
        {
            try
            {
                candidate.Refresh();
                if (candidate.Exists || candidate.LinkTarget is not null)
                    return true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return false;
    }
}

public static class FollowUpStorageCleanupCas
{
    public static void EnsureAffected(string transition, int actual, int expected)
    {
        if (actual != expected)
            throw new InvalidOperationException($"存储清理状态转换 {transition} 期望影响 {expected} 行，实际 {actual} 行；事务已回滚。");
    }
}

public enum FollowUpStorageCleanupRecoveryAction
{
    RestoreFiles,
    RestoreFilesAndCancelDatabase,
    DeleteQuarantine,
    StopForManualReview
}

public static class FollowUpStorageCleanupRecoveryPolicy
{
    public static FollowUpStorageCleanupRecoveryAction Decide(FollowUpStorageCleanupDatabaseState state) => state switch
    {
        FollowUpStorageCleanupDatabaseState.Original => FollowUpStorageCleanupRecoveryAction.RestoreFiles,
        FollowUpStorageCleanupDatabaseState.Prepared => FollowUpStorageCleanupRecoveryAction.RestoreFilesAndCancelDatabase,
        FollowUpStorageCleanupDatabaseState.Archived => FollowUpStorageCleanupRecoveryAction.DeleteQuarantine,
        _ => FollowUpStorageCleanupRecoveryAction.StopForManualReview
    };
}
