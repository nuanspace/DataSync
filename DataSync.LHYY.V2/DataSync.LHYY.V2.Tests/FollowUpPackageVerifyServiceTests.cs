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

    private async Task<PackageFixture> CreatePackageAsync(bool tamperSignature, bool addTraversalEntry)
    {
        Directory.CreateDirectory(_root);
        using var decryptRsa = RSA.Create(3072);
        using var signingRsa = RSA.Create(3072);
        var decryptPrivatePath = Path.Combine(_root, "decrypt-private.pem");
        var signingPublicPath = Path.Combine(_root, "signing-public.pem");
        await File.WriteAllTextAsync(decryptPrivatePath, decryptRsa.ExportRSAPrivateKeyPem());
        await File.WriteAllTextAsync(signingPublicPath, signingRsa.ExportSubjectPublicKeyInfoPem());

        var schemaSnapshot = Encoding.UTF8.GetBytes("{\"exportContractVersion\":\"1.0\",\"sourceDbFingerprint\":\"db\",\"generatedAt\":\"2026-07-14T10:00:00+08:00\",\"tables\":[]}");
        var tableManifest = "[]"u8.ToArray();
        var schemaDiff = "{\"diffLevel\":\"Compatible\",\"recommendation\":\"direct-import\",\"snapshotHash\":\"x\",\"addedTables\":[],\"removedTables\":[],\"changedColumns\":[],\"changedConstraints\":[]}"u8.ToArray();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            hospitalCode = "H1", hospitalId = Guid.NewGuid(), packageId = "pkg-1", sequenceNo = 1,
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
        var checksums = string.Join("\n", files.OrderBy(item => item.Key).Select(item => $"{Hash(item.Value)}  {item.Key}")) + "\n";
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
        var envelope = Encoding.UTF8.GetBytes($"{{\"protocolVersion\":\"1.0\",\"packageId\":\"pkg-1\",\"hospitalCode\":\"H1\",\"keyId\":\"hospital-key\",\"keyWrapAlgorithm\":\"RSA-OAEP-SHA256\",\"payloadCipherAlgorithm\":\"AES-256-CBC\",\"payloadMacAlgorithm\":\"HMAC-SHA256\",\"signatureAlgorithm\":\"RSA-PSS-SHA256\",\"wrappedKeyMaterial\":\"{Convert.ToBase64String(wrapped)}\",\"iv\":\"{Convert.ToBase64String(iv)}\",\"payloadSha256\":\"{Hash(payload)}\",\"payloadHmacSha256\":\"{Convert.ToHexString(hmac).ToLowerInvariant()}\",\"plaintextLength\":{innerZip.Length},\"payloadLength\":{payload.Length},\"generatedAt\":\"2026-07-14T10:00:00+08:00\"}}");
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
            EncryptionKeyId = "hospital-key",
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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed record PackageFixture(string PackagePath, string PackageHash, FollowUpPackageImportOptions Options);
}
