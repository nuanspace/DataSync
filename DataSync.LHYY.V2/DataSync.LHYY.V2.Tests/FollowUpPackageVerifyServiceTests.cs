using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using DataSync.LHYY.V2.Services.FollowUp;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class FollowUpPackageVerifyServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "datasync-followup-verify", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task 完整加密包通过校验并解压到受限Staging()
    {
        var fixture = await CreatePackageAsync(false, false);
        var service = new FollowUpPackageVerifyService(fixture.Options);

        var result = await service.VerifyAndExtractAsync(
            fixture.PackagePath, fixture.PackageHash, "H1", CancellationToken.None);

        Assert.Equal("pkg-1", result.Manifest.PackageId);
        Assert.True(File.Exists(Path.Combine(result.StagingPath, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(result.StagingPath, "schema", "table-manifest.json")));
    }

    [Fact]
    public async Task 配置中的旧KeyId不影响使用当前私钥导入()
    {
        var fixture = await CreatePackageAsync(false, false, configuredEncryptionKeyId: "stale-key-id");
        var service = new FollowUpPackageVerifyService(fixture.Options);

        var result = await service.VerifyAndExtractAsync(
            fixture.PackagePath, fixture.PackageHash, "H1", CancellationToken.None);

        Assert.Equal("pkg-1", result.Manifest.PackageId);
    }

    [Fact]
    public async Task 包KeyId与当前解密私钥不一致时拒绝()
    {
        var fixture = await CreatePackageAsync(
            false,
            false,
            configuredEncryptionKeyId: "wrong-key-id",
            envelopeKeyId: "wrong-key-id");
        var service = new FollowUpPackageVerifyService(fixture.Options);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifyAndExtractAsync(
            fixture.PackagePath, fixture.PackageHash, "H1", CancellationToken.None));

        Assert.Contains("Key Id", exception.Message);
    }

    [Fact]
    public async Task 签名被篡改时在解密前拒绝()
    {
        var fixture = await CreatePackageAsync(true, false);
        var service = new FollowUpPackageVerifyService(fixture.Options);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifyAndExtractAsync(
            fixture.PackagePath, fixture.PackageHash, "H1", CancellationToken.None));

        Assert.Contains("签名", exception.Message);
        Assert.Empty(Directory.Exists(fixture.Options.StagingRoot)
            ? Directory.EnumerateDirectories(fixture.Options.StagingRoot)
            : []);
    }

    [Fact]
    public async Task 内层Zip包含路径穿越时拒绝且不写出逃逸文件()
    {
        var fixture = await CreatePackageAsync(false, true);
        var service = new FollowUpPackageVerifyService(fixture.Options);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifyAndExtractAsync(
            fixture.PackagePath, fixture.PackageHash, "H1", CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task 外层包哈希为空时拒绝导入()
    {
        var fixture = await CreatePackageAsync(false, false);
        var service = new FollowUpPackageVerifyService(fixture.Options);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifyAndExtractAsync(
            fixture.PackagePath, null, "H1", CancellationToken.None));

        Assert.Contains("SHA-256", exception.Message);
    }

    [Fact]
    public async Task 包内标识与文件名包号不一致时拒绝导入()
    {
        var fixture = await CreatePackageAsync(false, false, packageId: "pkg-2");
        var service = new FollowUpPackageVerifyService(fixture.Options);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifyAndExtractAsync(
            fixture.PackagePath, fixture.PackageHash, "H1", CancellationToken.None));

        Assert.Contains("包标识", exception.Message);
    }

    [Fact]
    public async Task Checksum路径逃逸时在读取文件前拒绝()
    {
        var externalPath = Path.Combine(_root, "outside.txt");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(externalPath, "outside");
        var relative = Path.GetRelativePath(Path.Combine(_root, "staging", "placeholder"), externalPath)
            .Replace('\\', '/');
        var fixture = await CreatePackageAsync(false, false, checksumExtraPath: relative);
        var service = new FollowUpPackageVerifyService(fixture.Options);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifyAndExtractAsync(
            fixture.PackagePath, fixture.PackageHash, "H1", CancellationToken.None));

        Assert.Contains("路径", exception.Message);
    }

    [Fact]
    public async Task 元数据路径在校验后被替换时仍反序列化已校验字节()
    {
        var stagingPath = Path.Combine(_root, "metadata-snapshot");
        var original = CreateMetadataFiles("pkg-original");
        await WriteMetadataFilesAsync(stagingPath, original);

        var verifiedFiles = await FollowUpPackageVerifyService.VerifyChecksumsAndCaptureMetadataAsync(
            stagingPath,
            CancellationToken.None);

        var replacement = CreateMetadataFiles("pkg-replacement");
        await ReplaceMetadataFilesAsync(stagingPath, replacement);
        var metadata = FollowUpPackageVerifyService.DeserializeVerifiedMetadata(verifiedFiles);

        Assert.Equal("pkg-original", metadata.Manifest.PackageId);
        var replacementManifest = JsonSerializer.Deserialize<FollowUpPackageManifest>(
            await File.ReadAllBytesAsync(Path.Combine(stagingPath, "manifest.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("pkg-replacement", replacementManifest!.PackageId);
    }

    private async Task<PackageFixture> CreatePackageAsync(
        bool tamperSignature,
        bool addTraversalEntry,
        string packageId = "pkg-1",
        string? checksumExtraPath = null,
        string? configuredEncryptionKeyId = null,
        string? envelopeKeyId = null)
    {
        Directory.CreateDirectory(_root);
        using var decryptRsa = RSA.Create(3072);
        using var signingRsa = RSA.Create(3072);
        var decryptPrivatePath = Path.Combine(_root, "decrypt-private.pem");
        var signingPublicPath = Path.Combine(_root, "signing-public.pem");
        await File.WriteAllTextAsync(decryptPrivatePath, decryptRsa.ExportRSAPrivateKeyPem());
        await File.WriteAllTextAsync(signingPublicPath, signingRsa.ExportSubjectPublicKeyInfoPem());
        var actualEncryptionKeyId = ComputeKeyId(decryptRsa);

        var schemaSnapshot = Encoding.UTF8.GetBytes("{\"exportContractVersion\":\"1.0\",\"sourceDbFingerprint\":\"db\",\"generatedAt\":\"2026-07-14T10:00:00+08:00\",\"tables\":[]}");
        var tableManifest = "[]"u8.ToArray();
        var schemaDiff = "{\"diffLevel\":\"Compatible\",\"recommendation\":\"direct-import\",\"snapshotHash\":\"x\",\"addedTables\":[],\"removedTables\":[],\"changedColumns\":[],\"changedConstraints\":[]}"u8.ToArray();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            hospitalCode = "H1", hospitalId = Guid.NewGuid(), packageId, sequenceNo = 1,
            packageType = "Baseline", fromWatermark = (DateTime?)null, toWatermark = new DateTime(2026, 7, 14, 10, 0, 0),
            previousPackageId = (string?)null, relatedPackageId = (string?)null, generatedAt = DateTimeOffset.Now,
            exportContractVersion = "1.0", minImporterVersion = "1.0.0", sourceDbFingerprint = "db",
            schemaSnapshotHash = Hash(schemaSnapshot), tableManifestHash = Hash(tableManifest), schemaDiffHash = Hash(schemaDiff),
            dataFiles = Array.Empty<object>(), attachmentFiles = Array.Empty<object>(), recordCounts = new { }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var files = new Dictionary<string, byte[]>
        {
            ["manifest.json"] = manifest,
            ["schema/schema-snapshot.json"] = schemaSnapshot,
            ["schema/table-manifest.json"] = tableManifest,
            ["schema/schema-diff.json"] = schemaDiff
        };
        if (addTraversalEntry) files["../escape.txt"] = "escape"u8.ToArray();
        var checksumLines = files.OrderBy(item => item.Key).Select(item => $"{Hash(item.Value)}  {item.Key}").ToList();
        if (checksumExtraPath is not null)
            checksumLines.Add($"{Hash("outside"u8.ToArray())}  {checksumExtraPath}");
        var checksums = string.Join("\n", checksumLines) + "\n";
        files["checksums.sha256"] = Encoding.UTF8.GetBytes(checksums);

        byte[] innerZip;
        await using (var inner = new MemoryStream())
        {
            using (var archive = new ZipArchive(inner, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in files)
                {
                    var entry = archive.CreateEntry(file.Key);
                    await using var stream = entry.Open();
                    await stream.WriteAsync(file.Value);
                }
            }
            innerZip = inner.ToArray();
        }

        var keyMaterial = RandomNumberGenerator.GetBytes(64);
        var iv = RandomNumberGenerator.GetBytes(16);
        byte[] payload;
        using (var aes = Aes.Create())
        {
            aes.Key = keyMaterial[..32]; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using var output = new MemoryStream();
            await using (var crypto = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
            {
                await crypto.WriteAsync(innerZip);
                crypto.FlushFinalBlock();
            }
            payload = output.ToArray();
        }
        byte[] hmac;
        using (var mac = new HMACSHA256(keyMaterial[32..])) hmac = mac.ComputeHash([.. iv, .. payload]);
        var wrapped = decryptRsa.Encrypt(keyMaterial, RSAEncryptionPadding.OaepSHA256);
        var envelope = Encoding.UTF8.GetBytes($"{{\"protocolVersion\":\"1.0\",\"packageId\":\"{packageId}\",\"hospitalCode\":\"H1\",\"keyId\":\"{envelopeKeyId ?? actualEncryptionKeyId}\",\"keyWrapAlgorithm\":\"RSA-OAEP-SHA256\",\"payloadCipherAlgorithm\":\"AES-256-CBC\",\"payloadMacAlgorithm\":\"HMAC-SHA256\",\"signatureAlgorithm\":\"RSA-PSS-SHA256\",\"wrappedKeyMaterial\":\"{Convert.ToBase64String(wrapped)}\",\"iv\":\"{Convert.ToBase64String(iv)}\",\"payloadSha256\":\"{Hash(payload)}\",\"payloadHmacSha256\":\"{Convert.ToHexString(hmac).ToLowerInvariant()}\",\"plaintextLength\":{innerZip.Length},\"payloadLength\":{payload.Length},\"generatedAt\":\"2026-07-14T10:00:00+08:00\"}}");
        var signature = signingRsa.SignData(envelope, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        if (tamperSignature) signature[0] ^= 0xff;

        var packagePath = Path.Combine(_root, "pkg-1.fupkg");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "envelope.json", envelope);
            WriteEntry(archive, "payload.bin", payload);
            WriteEntry(archive, "signature.bin", signature);
        }
        var packageHash = Hash(await File.ReadAllBytesAsync(packagePath));
        return new PackageFixture(packagePath, packageHash, new FollowUpPackageImportOptions
        {
            StagingRoot = Path.Combine(_root, "staging"),
            DecryptionPrivateKeyPath = decryptPrivatePath,
            CloudSigningPublicKeyPath = signingPublicPath,
            EncryptionKeyId = configuredEncryptionKeyId ?? actualEncryptionKeyId,
            MaxPackageBytes = 64 * 1024 * 1024,
            MaxExpandedBytes = 128 * 1024 * 1024
        });
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] value)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(value);
    }

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static string ComputeKeyId(RSA rsa)
        => $"rsa-sha256-{Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant()}";

    private static Dictionary<string, byte[]> CreateMetadataFiles(string packageId)
    {
        var schemaSnapshot = JsonSerializer.SerializeToUtf8Bytes(
            new FollowUpSchemaSnapshot
            {
                ExportContractVersion = "1.0",
                SourceDbFingerprint = packageId,
                GeneratedAt = DateTimeOffset.Parse("2026-07-14T10:00:00+08:00")
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var tableManifest = JsonSerializer.SerializeToUtf8Bytes(
            new List<FollowUpTableManifestItem>(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var schemaDiff = JsonSerializer.SerializeToUtf8Bytes(
            new FollowUpSchemaDiff
            {
                DiffLevel = "Compatible",
                Recommendation = "direct-import",
                SnapshotHash = packageId
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var manifest = JsonSerializer.SerializeToUtf8Bytes(
            new FollowUpPackageManifest
            {
                HospitalCode = "H1",
                HospitalId = Guid.NewGuid(),
                PackageId = packageId,
                SequenceNo = 1,
                PackageType = "Baseline",
                ToWatermark = new DateTime(2026, 7, 14, 10, 0, 0),
                GeneratedAt = DateTimeOffset.Parse("2026-07-14T10:00:00+08:00"),
                ExportContractVersion = "1.0",
                MinImporterVersion = "1.0.0",
                SourceDbFingerprint = packageId,
                SchemaSnapshotHash = Hash(schemaSnapshot),
                TableManifestHash = Hash(tableManifest),
                SchemaDiffHash = Hash(schemaDiff)
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new Dictionary<string, byte[]>
        {
            ["manifest.json"] = manifest,
            ["schema/schema-snapshot.json"] = schemaSnapshot,
            ["schema/table-manifest.json"] = tableManifest,
            ["schema/schema-diff.json"] = schemaDiff
        };
    }

    private static async Task WriteMetadataFilesAsync(
        string stagingPath,
        IReadOnlyDictionary<string, byte[]> files)
    {
        Directory.CreateDirectory(stagingPath);
        foreach (var file in files)
        {
            var path = Path.Combine(stagingPath, file.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, file.Value);
        }
        var checksums = string.Join(
            "\n",
            files.OrderBy(item => item.Key).Select(item => $"{Hash(item.Value)}  {item.Key}")) + "\n";
        await File.WriteAllTextAsync(
            Path.Combine(stagingPath, "checksums.sha256"),
            checksums,
            new UTF8Encoding(false));
    }

    private static async Task ReplaceMetadataFilesAsync(
        string stagingPath,
        IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(stagingPath, file.Key.Replace('/', Path.DirectorySeparatorChar));
            var replacementPath = path + ".replacement";
            await File.WriteAllBytesAsync(replacementPath, file.Value);
            File.Move(replacementPath, path, overwrite: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed record PackageFixture(string PackagePath, string PackageHash, FollowUpPackageImportOptions Options);
}
