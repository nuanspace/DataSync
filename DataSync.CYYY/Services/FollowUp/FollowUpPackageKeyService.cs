using DataSync.CYYY.Models.FollowUp;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace DataSync.CYYY.Services.FollowUp;

public sealed class FollowUpPackageKeyService(IOptions<FollowUpPackageSyncOptions> options)
{
    private readonly FollowUpPackageSyncOptions _options = options.Value;

    public async Task<string> GenerateKeyAsync(CancellationToken cancellationToken = default)
    {
        var privatePath = Path.GetFullPath(_options.PrivateKeyPath);
        var publicPath = privatePath + ".pub";
        if (File.Exists(privatePath) || File.Exists(publicPath))
            throw new InvalidOperationException("SSH 密钥已存在，禁止直接覆盖；请先按轮换流程处理旧密钥。");
        Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("ssh-keygen")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-q");
        process.StartInfo.ArgumentList.Add("-t");
        process.StartInfo.ArgumentList.Add("ed25519");
        process.StartInfo.ArgumentList.Add("-N");
        process.StartInfo.ArgumentList.Add(string.Empty);
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add("datasync-followup-dmz");
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(privatePath);
        if (!process.Start()) throw new InvalidOperationException("无法启动 ssh-keygen。");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"SSH 密钥生成失败：{await errorTask}");
        RestrictFile(privatePath);
        return await File.ReadAllTextAsync(publicPath, cancellationToken);
    }

    public async Task SaveKnownHostsAsync(string content, CancellationToken cancellationToken = default)
    {
        var normalized = content.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || (!normalized.Contains("ssh-ed25519", StringComparison.Ordinal)
                && !normalized.Contains("ecdsa-sha2-", StringComparison.Ordinal)
                && !normalized.Contains("ssh-rsa", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("known-hosts 内容不是受支持的 SSH host key 记录。");
        }
        var path = Path.GetFullPath(_options.KnownHostsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, normalized + Environment.NewLine, cancellationToken);
        RestrictFile(path);
    }

    public async Task<string?> ReadPublicKeyAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(_options.PrivateKeyPath) + ".pub";
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
    }

    public FollowUpKeyPreflight GetPreflight() => new(
        File.Exists(_options.PrivateKeyPath),
        File.Exists(_options.PrivateKeyPath + ".pub"),
        File.Exists(_options.KnownHostsPath),
        File.Exists(_options.TokenFilePath));

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

public sealed record FollowUpKeyPreflight(bool PrivateKeyReady, bool PublicKeyReady, bool KnownHostsReady, bool TokenReady)
{
    public bool Ready => PrivateKeyReady && PublicKeyReady && KnownHostsReady && TokenReady;
}
