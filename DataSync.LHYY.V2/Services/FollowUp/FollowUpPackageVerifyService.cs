using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataSync.LHYY.V2.Services.FollowUp;

public sealed class FollowUpPackageVerifyService
{
    private static readonly HashSet<string> OuterEntries = ["envelope.json", "payload.bin", "signature.bin"];
    private readonly FollowUpPackageImportOptions _options;

    public FollowUpPackageVerifyService(IOptions<FollowUpPackageImportOptions> options) : this(options.Value) { }
    public FollowUpPackageVerifyService(FollowUpPackageImportOptions options) => _options = options;

    public async Task<FollowUpVerifiedPackage> VerifyAndExtractAsync(
        string packagePath,
        string? expectedPackageHash,
        string expectedHospitalCode,
        CancellationToken cancellationToken = default)
        => await VerifyAndExtractAsync(
            packagePath,
            expectedPackageHash,
            expectedHospitalCode,
            Path.GetFileNameWithoutExtension(packagePath),
            null,
            null,
            cancellationToken);

    public async Task<FollowUpVerifiedPackage> VerifyAndExtractAsync(
        string packagePath,
        string? expectedPackageHash,
        string expectedHospitalCode,
        string expectedPackageId,
        long? expectedSequenceNo,
        string? expectedPackageType,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("数据包不存在。", packagePath);
        var packageInfo = new FileInfo(packagePath);
        if (packageInfo.Length <= 0 || packageInfo.Length > _options.MaxPackageBytes)
            throw new InvalidDataException("数据包大小无效或超过限制。");
        if (string.IsNullOrWhiteSpace(expectedPackageHash)
            || expectedPackageHash.Length != 64
            || expectedPackageHash.Any(value => !Uri.IsHexDigit(value)))
            throw new InvalidDataException("外层数据包 SHA-256 不能为空且必须为 64 位十六进制值。");
        var actualPackageHash = await HashFileAsync(packagePath, cancellationToken);
        if (!string.Equals(actualPackageHash, expectedPackageHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("外层数据包 SHA-256 校验失败。");

        var stagingPath = Path.Combine(Path.GetFullPath(_options.StagingRoot), $"verify-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingPath);
            RestrictDirectory(stagingPath);
            byte[] envelopeBytes;
            byte[] signatureBytes;
            using (var outer = ZipFile.OpenRead(packagePath))
            {
                var names = outer.Entries.Select(item => item.FullName).ToList();
                if (names.Count != OuterEntries.Count
                    || names.Distinct(StringComparer.Ordinal).Count() != names.Count
                    || !names.ToHashSet(StringComparer.Ordinal).SetEquals(OuterEntries))
                    throw new InvalidDataException("外层包必须且只能包含 envelope.json、payload.bin、signature.bin。");
                envelopeBytes = await ReadEntryAsync(outer.GetEntry("envelope.json")!, 64 * 1024, cancellationToken);
                signatureBytes = await ReadEntryAsync(outer.GetEntry("signature.bin")!, 16 * 1024, cancellationToken);
                VerifySignature(envelopeBytes, signatureBytes);
                var parsedEnvelope = FollowUpEnvelopeParser.ParseAndValidate(envelopeBytes);
                if (!string.Equals(parsedEnvelope.HospitalCode, expectedHospitalCode, StringComparison.Ordinal)
                    || !string.Equals(parsedEnvelope.PackageId, expectedPackageId, StringComparison.Ordinal)
                    || (!string.IsNullOrWhiteSpace(_options.EncryptionKeyId)
                        && !string.Equals(parsedEnvelope.KeyId, _options.EncryptionKeyId, StringComparison.Ordinal)))
                    throw new InvalidDataException("envelope 医院编码、包标识或 Key Id 与待导入记录不匹配。");

                var keyMaterial = UnwrapKey(parsedEnvelope.WrappedKeyMaterial);
                try
                {
                    if (keyMaterial.Length != 64) throw new InvalidDataException("解包后的密钥材料长度无效。");
                    var iv = Convert.FromBase64String(parsedEnvelope.Iv);
                    if (iv.Length != 16) throw new InvalidDataException("payload IV 长度无效。");

                    var encryptedPayloadPath = Path.Combine(stagingPath, ".payload.bin");
                    await CopyAndVerifyPayloadAsync(
                        outer.GetEntry("payload.bin")!, encryptedPayloadPath, keyMaterial[32..], iv, parsedEnvelope, cancellationToken);
                    var innerZipPath = Path.Combine(stagingPath, ".inner.zip");
                    await DecryptAsync(encryptedPayloadPath, keyMaterial[..32], iv, innerZipPath, parsedEnvelope.PlaintextLength, cancellationToken);
                    File.Delete(encryptedPayloadPath);
                    await ExtractInnerAsync(innerZipPath, stagingPath, cancellationToken);
                    File.Delete(innerZipPath);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(keyMaterial);
                }
            }

            var envelope = FollowUpEnvelopeParser.ParseAndValidate(envelopeBytes);

            await VerifyChecksumsAsync(stagingPath, cancellationToken);
            var manifest = await ReadJsonAsync<FollowUpPackageManifest>(Path.Combine(stagingPath, "manifest.json"), cancellationToken);
            if (manifest.HospitalCode != expectedHospitalCode
                || manifest.PackageId != envelope.PackageId
                || manifest.PackageId != expectedPackageId
                || expectedSequenceNo.HasValue && manifest.SequenceNo != expectedSequenceNo.Value
                || expectedPackageType is not null && !string.Equals(manifest.PackageType, expectedPackageType, StringComparison.Ordinal))
                throw new InvalidDataException("manifest 与待导入记录的医院、包标识、序号或包类型不一致。");
            var snapshotPath = Path.Combine(stagingPath, "schema", "schema-snapshot.json");
            var tableManifestPath = Path.Combine(stagingPath, "schema", "table-manifest.json");
            var diffPath = Path.Combine(stagingPath, "schema", "schema-diff.json");
            if (await HashFileAsync(snapshotPath, cancellationToken) != manifest.SchemaSnapshotHash
                || await HashFileAsync(tableManifestPath, cancellationToken) != manifest.TableManifestHash
                || await HashFileAsync(diffPath, cancellationToken) != manifest.SchemaDiffHash)
                throw new InvalidDataException("结构文件 hash 与 manifest 不一致。");

            return new FollowUpVerifiedPackage(
                packagePath,
                actualPackageHash,
                stagingPath,
                envelope,
                manifest,
                await ReadJsonAsync<List<FollowUpTableManifestItem>>(tableManifestPath, cancellationToken),
                await ReadJsonAsync<FollowUpSchemaSnapshot>(snapshotPath, cancellationToken),
                await ReadJsonAsync<FollowUpSchemaDiff>(diffPath, cancellationToken));
        }
        catch
        {
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, true);
            throw;
        }
    }

    private void VerifySignature(byte[] envelopeBytes, byte[] signatureBytes)
    {
        if (!File.Exists(_options.CloudSigningPublicKeyPath)) throw new InvalidOperationException("云端验签公钥不存在。");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(_options.CloudSigningPublicKeyPath));
        if (!rsa.VerifyData(envelopeBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("envelope 签名校验失败。");
    }

    private byte[] UnwrapKey(string wrappedKey)
    {
        if (!File.Exists(_options.DecryptionPrivateKeyPath)) throw new InvalidOperationException("院内解密私钥不存在。");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(_options.DecryptionPrivateKeyPath));
        try { return rsa.Decrypt(Convert.FromBase64String(wrappedKey), RSAEncryptionPadding.OaepSHA256); }
        catch (CryptographicException ex) { throw new InvalidDataException("包密钥解包失败。", ex); }
    }

    private async Task CopyAndVerifyPayloadAsync(
        ZipArchiveEntry entry,
        string destination,
        byte[] hmacKey,
        byte[] iv,
        FollowUpEncryptedEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (entry.Length != envelope.PayloadLength
            || entry.Length < 0
            || entry.Length > _options.MaxPackageBytes
            || entry.CompressedLength > _options.MaxPackageBytes)
            throw new InvalidDataException("payload 长度无效或超过限制。");

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, hmacKey);
        hmac.AppendData(iv);
        await using var input = entry.Open();
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            copied += read;
            if (copied > envelope.PayloadLength) throw new InvalidDataException("payload 实际长度超过 envelope 声明。 ");
            sha.AppendData(buffer, 0, read);
            hmac.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
        var actualHash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        var actualHmac = hmac.GetHashAndReset();
        var expectedHmac = Convert.FromHexString(envelope.PayloadHmacSha256);
        if (copied != envelope.PayloadLength
            || !string.Equals(actualHash, envelope.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("payload 长度或 SHA-256 校验失败。");
        if (expectedHmac.Length != actualHmac.Length
            || !CryptographicOperations.FixedTimeEquals(actualHmac, expectedHmac))
            throw new InvalidDataException("payload HMAC 校验失败。");
    }

    private static async Task DecryptAsync(string payloadPath, byte[] key, byte[] iv, string destination, long expectedLength, CancellationToken cancellationToken)
    {
        using var aes = Aes.Create();
        aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
        await using var input = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        await using var crypto = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        try { await crypto.CopyToAsync(output, cancellationToken); }
        catch (CryptographicException ex) { throw new InvalidDataException("payload 解密失败。", ex); }
        await output.FlushAsync(cancellationToken);
        if (output.Length != expectedLength) throw new InvalidDataException("解密后明文长度不一致。");
    }

    private async Task ExtractInnerAsync(string zipPath, string stagingPath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > _options.MaxArchiveEntries) throw new InvalidDataException("内层 ZIP 条目数量超过限制。");
        long expanded = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalized)
                || normalized.StartsWith('/')
                || normalized.Contains(':')
                || normalized.Split('/').Any(part => part is ".." or ".")
                || !seen.Add(normalized))
                throw new InvalidDataException("内层 ZIP 包含非法或重复路径。");
            expanded += entry.Length;
            if (expanded > _options.MaxExpandedBytes) throw new InvalidDataException("内层 ZIP 解压总量超过限制。");
            var destination = Path.GetFullPath(Path.Combine(stagingPath, normalized.Replace('/', Path.DirectorySeparatorChar)));
            var rootPrefix = Path.GetFullPath(stagingPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(rootPrefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("内层 ZIP 路径逃逸 staging 目录。");
            if (normalized.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static async Task VerifyChecksumsAsync(string stagingPath, CancellationToken cancellationToken)
    {
        var checksumPath = Path.Combine(stagingPath, "checksums.sha256");
        if (!File.Exists(checksumPath)) throw new InvalidDataException("内层包缺少 checksums.sha256。");
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in await File.ReadAllLinesAsync(checksumPath, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64) throw new InvalidDataException("checksums.sha256 格式无效。");
            var expected = line[..separator];
            var relative = NormalizeChecksumPath(line[(separator + 2)..]);
            if (!declared.Add(relative)) throw new InvalidDataException("checksums.sha256 包含重复文件。");
            var path = SafeStagingFilePath(stagingPath, relative);
            if (!File.Exists(path) || await HashFileAsync(path, cancellationToken) != expected)
                throw new InvalidDataException($"内层文件校验失败：{relative}");
        }
        var actual = Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(stagingPath, path))
            .Where(path => !string.Equals(path, "checksums.sha256", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(declared)) throw new InvalidDataException("内层包包含未声明文件或清单缺项。");
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new InvalidDataException($"内层包缺少 {Path.GetFileName(path)}。");
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, FollowUpJson.Options, cancellationToken)
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} 内容为空。");
    }

    private static string NormalizeChecksumPath(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)
            || Path.IsPathRooted(normalized)
            || normalized.StartsWith('/')
            || normalized.Contains(':')
            || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("checksums.sha256 包含非法文件路径。");
        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string SafeStagingFilePath(string stagingPath, string relative)
    {
        var fullRoot = Path.GetFullPath(stagingPath);
        var target = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("checksums.sha256 文件路径逃逸 staging 目录。");
        return target;
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, long maxBytes, CancellationToken cancellationToken)
    {
        if (entry.Length < 0 || entry.Length > maxBytes || entry.CompressedLength > maxBytes)
            throw new InvalidDataException($"ZIP 条目 {entry.FullName} 超过限制。");
        await using var stream = entry.Open();
        using var output = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        await stream.CopyToAsync(output, cancellationToken);
        return output.ToArray();
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
