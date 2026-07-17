using DataSync.LHYY.V2.Models.FollowUp;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace DataSync.LHYY.V2.Services.FollowUp;

public sealed class FollowUpPackageImportKeyService(IOptions<FollowUpPackageImportOptions> options)
{
    private readonly FollowUpPackageImportOptions _options = options.Value;

    public async Task<string> GenerateEncryptionKeyAsync(CancellationToken cancellationToken = default)
    {
        var privatePath = Path.GetFullPath(_options.DecryptionPrivateKeyPath);
        var publicPath = privatePath + ".public.pem";
        if (File.Exists(privatePath) || File.Exists(publicPath))
            throw new InvalidOperationException("院内加密密钥已存在，禁止直接覆盖；请使用明确的密钥轮换流程。");
        Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
        using var rsa = RSA.Create(3072);
        await File.WriteAllTextAsync(privatePath, rsa.ExportRSAPrivateKeyPem(), cancellationToken);
        await File.WriteAllTextAsync(publicPath, rsa.ExportSubjectPublicKeyInfoPem(), cancellationToken);
        Restrict(privatePath);
        return await File.ReadAllTextAsync(publicPath, cancellationToken);
    }

    public async Task SaveSigningPublicKeyAsync(string pem, CancellationToken cancellationToken = default)
    {
        using var rsa = RSA.Create();
        try { rsa.ImportFromPem(pem); }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException("云端验签公钥不是有效 RSA PEM。", ex);
        }
        if (rsa.KeySize < 2048) throw new InvalidOperationException("云端验签公钥强度不足 2048 位。");
        var path = Path.GetFullPath(_options.CloudSigningPublicKeyPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, pem.Trim() + Environment.NewLine, cancellationToken);
        Restrict(path);
    }

    public async Task<string?> ReadEncryptionPublicKeyAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(_options.DecryptionPrivateKeyPath) + ".public.pem";
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
    }

    private static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
