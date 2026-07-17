using System.Security.Cryptography;

namespace DataSync.CYYY.Services.FollowUp;

public sealed class FollowUpPackageFileStore
{
    public async Task<string> SaveAsync(
        string packageRoot,
        string packageId,
        Stream source,
        long expectedSize,
        string? expectedSha256,
        long maxPackageBytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packageId)
            || packageId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || packageId.Contains(Path.DirectorySeparatorChar)
            || packageId.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException("包标识不能用于本地文件名。");
        }
        if (expectedSize < 0 || expectedSize > maxPackageBytes)
            throw new InvalidDataException("数据包大小超过允许上限。");

        Directory.CreateDirectory(packageRoot);
        var finalPath = Path.Combine(packageRoot, $"{packageId}.fupkg");
        var partialPath = Path.Combine(packageRoot, $".{packageId}.{Guid.NewGuid():N}.partial");
        try
        {
            await using (var target = new FileStream(
                             partialPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 1024];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > maxPackageBytes)
                        throw new InvalidDataException("数据包大小超过允许上限。");
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await target.FlushAsync(cancellationToken);
                target.Flush(flushToDisk: true);

                if (total != expectedSize)
                    throw new InvalidDataException($"数据包长度不一致，期望 {expectedSize}，实际 {total}。");
                var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(expectedSha256)
                    && !string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("数据包 SHA-256 校验失败。");
                }
            }

            File.Move(partialPath, finalPath, overwrite: true);
            return finalPath;
        }
        catch
        {
            if (File.Exists(partialPath)) File.Delete(partialPath);
            throw;
        }
    }
}
