using System.Diagnostics;
using System.Security.Cryptography;
using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using Microsoft.Extensions.Options;

namespace DataSync.LHYY.V2.Services.FollowUp;

public sealed class FollowUpHospitalInitializationService(
    IOptions<FollowUpPackageImportOptions> options,
    FollowUpPackageImportKeyService importKeyService)
{
    private readonly FollowUpPackageImportOptions _options = options.Value;

    public FollowUpHospitalInitializationStatus GetStatus() => new(
        IsNonEmpty(_options.CyyyPrivateKeyPath),
        IsNonEmpty(_options.CyyyPrivateKeyPath + ".pub"),
        IsNonEmpty(_options.DecryptionPrivateKeyPath),
        IsNonEmpty(_options.DecryptionPrivateKeyPath + ".public.pem"),
        IsNonEmpty(_options.CyyyKnownHostsPath),
        IsNonEmpty(_options.CyyyTokenFilePath),
        IsNonEmpty(_options.CloudSigningPublicKeyPath));

    public async Task<string> GenerateAndExportAsync(CancellationToken ct = default)
    {
        ValidateIdentity();
        var status = GetStatus();
        if (!status.CyyyPrivateKeyReady && !status.CyyyPublicKeyReady)
            await GenerateCyyyKeyAsync(ct);
        else if (!status.CyyyPrivateKeyReady || !status.CyyyPublicKeyReady)
            throw new InvalidOperationException("CYYY SSH 密钥不完整，禁止自动覆盖，请先按故障恢复流程处理。");
        await ChangeCyyyOwnershipAsync([_options.CyyyPrivateKeyPath, _options.CyyyPrivateKeyPath + ".pub"], ct);

        status = GetStatus();
        if (!status.LhyyPrivateKeyReady && !status.LhyyPublicKeyReady)
            await importKeyService.GenerateEncryptionKeyAsync(ct);
        else if (!status.LhyyPrivateKeyReady || !status.LhyyPublicKeyReady)
            throw new InvalidOperationException("LHYY 加密密钥不完整，禁止自动覆盖，请先按故障恢复流程处理。");

        var cyyyPublicKey = (await File.ReadAllTextAsync(_options.CyyyPrivateKeyPath + ".pub", ct)).Trim();
        var lhyyPublicKey = (await importKeyService.ReadEncryptionPublicKeyAsync(ct))
                            ?? throw new InvalidOperationException("LHYY 加密公钥不存在。");
        return FollowUpInitializationPackageSerializer.Serialize(new FollowUpInitializationPackage
        {
            PackageType = FollowUpInitializationPackageTypes.HospitalToDmz,
            HospitalId = ParseHospitalId(_options.HospitalId).ToString(),
            HospitalCode = _options.HospitalCode.Trim(),
            DeviceId = _options.DeviceId.Trim(),
            CyyySshPublicKey = cyyyPublicKey,
            LhyyEncryptionPublicKey = lhyyPublicKey.Trim(),
            LhyyEncryptionKeyId = ComputeRsaKeyId(lhyyPublicKey)
        });
    }

    public async Task ImportDmzResponseAsync(string json, CancellationToken ct = default)
    {
        ValidateIdentity();
        var package = FollowUpInitializationPackageSerializer.Deserialize(json, FollowUpInitializationPackageTypes.DmzToHospital);
        ValidateIdentity(package);
        if (string.IsNullOrWhiteSpace(package.DmzHostKnownHostsLine)
            || string.IsNullOrWhiteSpace(package.DmzInnerDeviceToken)
            || string.IsNullOrWhiteSpace(package.CloudSigningPublicKey))
            throw new InvalidDataException("DMZ 回程包缺少主机指纹、院内访问令牌或云端验签公钥。");

        ValidateKnownHostsLine(package.DmzHostKnownHostsLine);
        ValidateToken(package.DmzInnerDeviceToken);
        var signingKeyId = ComputeRsaKeyId(package.CloudSigningPublicKey);
        if (!string.IsNullOrWhiteSpace(package.CloudSigningKeyId)
            && !string.Equals(package.CloudSigningKeyId, signingKeyId, StringComparison.Ordinal))
            throw new InvalidDataException("云端签名公钥与初始化包中的 KeyId 不一致。");

        await EnsureMissingOrSameAsync(_options.CyyyKnownHostsPath, package.DmzHostKnownHostsLine, "DMZ 主机指纹", ct);
        await EnsureMissingOrSameAsync(_options.CyyyTokenFilePath, package.DmzInnerDeviceToken, "DMZ 访问令牌", ct);
        await EnsureMissingOrSameAsync(_options.CloudSigningPublicKeyPath, package.CloudSigningPublicKey, "云端验签公钥", ct);
        await WriteSecretIfMissingAsync(_options.CyyyKnownHostsPath, package.DmzHostKnownHostsLine, ct);
        await WriteSecretIfMissingAsync(_options.CyyyTokenFilePath, package.DmzInnerDeviceToken, ct);
        await ChangeCyyyOwnershipAsync([_options.CyyyKnownHostsPath, _options.CyyyTokenFilePath], ct);
        if (!IsNonEmpty(_options.CloudSigningPublicKeyPath))
            await importKeyService.SaveSigningPublicKeyAsync(package.CloudSigningPublicKey, ct);
    }

    private async Task GenerateCyyyKeyAsync(CancellationToken ct)
    {
        var privatePath = Path.GetFullPath(_options.CyyyPrivateKeyPath);
        Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("ssh-keygen")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in new[] { "-q", "-t", "ed25519", "-N", string.Empty, "-C", "datasync-followup-dmz", "-f", privatePath })
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException("无法启动 ssh-keygen。");
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new InvalidOperationException($"CYYY SSH 密钥生成失败：{await errorTask}");
        Restrict(privatePath);
        await ChangeCyyyOwnershipAsync([privatePath, privatePath + ".pub"], ct);
    }

    private void ValidateIdentity()
    {
        if (!Guid.TryParse(_options.HospitalId, out _)
            || string.IsNullOrWhiteSpace(_options.HospitalCode)
            || string.IsNullOrWhiteSpace(_options.DeviceId))
            throw new InvalidOperationException("请先配置 HospitalId、HospitalCode 和 DeviceId。");
    }

    private void ValidateIdentity(FollowUpInitializationPackage package)
    {
        if (!Guid.TryParse(package.HospitalId, out var packageHospitalId)
            || packageHospitalId != ParseHospitalId(_options.HospitalId)
            || !string.Equals(package.HospitalCode, _options.HospitalCode, StringComparison.Ordinal)
            || !string.Equals(package.DeviceId, _options.DeviceId, StringComparison.Ordinal))
            throw new InvalidDataException("初始化包的医院或设备身份与本机配置不一致。");
    }

    private static string ComputeRsaKeyId(string pem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return $"rsa-sha256-{Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant()}";
    }

    private static Guid ParseHospitalId(string value)
        => Guid.TryParse(value, out var hospitalId)
            ? hospitalId
            : throw new InvalidOperationException("HospitalId 必须是有效 GUID。");

    private static void ValidateKnownHostsLine(string value)
    {
        if (value.Contains('\r') || value.Contains('\n')) throw new InvalidDataException("DMZ known_hosts 必须是单行记录。");
        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[1] is not ("ssh-ed25519" or "ssh-rsa") || string.IsNullOrWhiteSpace(parts[0]))
            throw new InvalidDataException("DMZ known_hosts 格式无效。");
        try { _ = Convert.FromBase64String(parts[2]); }
        catch (FormatException ex) { throw new InvalidDataException("DMZ known_hosts 公钥内容无效。", ex); }
    }

    private static void ValidateToken(string value)
    {
        var token = value.Trim();
        if (token.Length is < 32 or > 512 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new InvalidDataException("DMZ 访问令牌格式无效。");
    }

    private static async Task WriteSecretAsync(string path, string content, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, ct);
        Restrict(path);
    }

    private static async Task EnsureMissingOrSameAsync(string path, string expected, string label, CancellationToken ct)
    {
        if (!IsNonEmpty(path)) return;
        var existing = (await File.ReadAllTextAsync(path, ct)).Trim();
        if (!string.Equals(existing, expected.Trim(), StringComparison.Ordinal))
            throw new InvalidDataException($"{label}已存在且与导入包不同；禁止覆盖，请执行明确的轮换流程。");
    }

    private static Task WriteSecretIfMissingAsync(string path, string content, CancellationToken ct)
        => IsNonEmpty(path)
            ? Task.CompletedTask
            : WriteSecretAsync(path, content.Trim() + Environment.NewLine, ct);

    private static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static bool IsNonEmpty(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    private async Task ChangeCyyyOwnershipAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) return;
        if (_options.CyyyFileOwnerUid < 1 || _options.CyyyFileOwnerGid < 1)
            throw new InvalidOperationException("CYYY 文件属主 UID/GID 配置无效。");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("chown")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add($"{_options.CyyyFileOwnerUid}:{_options.CyyyFileOwnerGid}");
        foreach (var path in paths) process.StartInfo.ArgumentList.Add(Path.GetFullPath(path));
        if (!process.Start()) throw new InvalidOperationException("无法启动 chown 设置 CYYY secret 文件属主。");
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new InvalidOperationException($"设置 CYYY secret 文件属主失败：{await errorTask}");
    }
}
