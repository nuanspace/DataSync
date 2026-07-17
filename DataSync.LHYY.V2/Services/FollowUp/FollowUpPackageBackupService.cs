using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace DataSync.LHYY.V2.Services.FollowUp;

public sealed class FollowUpPackageBackupService(
    IConfiguration configuration,
    IOptions<FollowUpPackageImportOptions> options)
{
    private readonly string _cubeConnectionString = configuration.GetConnectionString("CubeDb")
        ?? throw new InvalidOperationException("未找到连接字符串 'CubeDb'");
    private readonly FollowUpPackageImportOptions _options = options.Value;

    public bool PostgreSqlToolsReady => FindExecutable("pg_dump") is not null && FindExecutable("pg_restore") is not null;

    public async Task<FollowUpBackupArtifact> CreateAsync(FollowUpVerifiedPackage package, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
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
            foreach (var attachment in package.Manifest.AttachmentFiles)
            {
                var relative = NormalizeAttachmentPath(attachment.Path);
                var source = SafeCombine(_options.AttachmentRoot, relative);
                var backup = SafeCombine(attachmentPath, relative);
                var existed = File.Exists(source);
                entries.Add(new AttachmentBackupEntry(relative, existed));
                if (!existed) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(source, backup, overwrite: false);
            }
            var manifestPath = Path.Combine(attachmentPath, "attachment-backup.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(entries, FollowUpJson.Options), cancellationToken);
            var hash = await HashFileAsync(databasePath, cancellationToken);
            var size = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            return new FollowUpBackupArtifact(id, root, databasePath, attachmentPath, hash, size);
        }
        catch
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            throw;
        }
    }

    public async Task RestoreAsync(FollowUpBackupArtifact artifact, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(artifact.DatabaseBackupPath)) throw new FileNotFoundException("数据库备份文件不存在。", artifact.DatabaseBackupPath);
        if (await HashFileAsync(artifact.DatabaseBackupPath, cancellationToken) != artifact.Hash)
            throw new InvalidDataException("数据库备份 hash 校验失败。");
        await RunPostgreSqlToolAsync("pg_restore", artifact.DatabaseBackupPath, restore: true, cancellationToken);
        await RestoreAttachmentsAsync(artifact.AttachmentBackupPath, cancellationToken);
    }

    public async Task RestoreAttachmentsAsync(string attachmentBackupPath, CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(attachmentBackupPath, "attachment-backup.json");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("附件备份清单不存在。", manifestPath);
        var entries = JsonSerializer.Deserialize<List<AttachmentBackupEntry>>(
                          await File.ReadAllTextAsync(manifestPath, cancellationToken), FollowUpJson.Options) ?? [];
        foreach (var entry in entries)
        {
            var target = SafeCombine(_options.AttachmentRoot, entry.RelativePath);
            var backup = SafeCombine(attachmentBackupPath, entry.RelativePath);
            if (entry.Existed)
            {
                if (!File.Exists(backup)) throw new FileNotFoundException("附件备份文件缺失。", backup);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(backup, target, overwrite: true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
    }

    public async Task InstallAttachmentsAsync(FollowUpVerifiedPackage package, CancellationToken cancellationToken = default)
    {
        foreach (var attachment in package.Manifest.AttachmentFiles)
        {
            var relative = NormalizeAttachmentPath(attachment.Path);
            var source = SafeCombine(package.StagingPath, attachment.Path.Replace('/', Path.DirectorySeparatorChar));
            var target = SafeCombine(_options.AttachmentRoot, relative);
            if (!File.Exists(source)) throw new FileNotFoundException("包内附件不存在。", source);
            if (new FileInfo(source).Length != attachment.SizeBytes || await HashFileAsync(source, cancellationToken) != attachment.Hash)
                throw new InvalidDataException($"附件校验失败：{relative}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + $".{Guid.NewGuid():N}.tmp";
            File.Copy(source, temporary, overwrite: false);
            File.Move(temporary, target, overwrite: true);
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
        using var registration = cancellationToken.Register(() => TryKill(process));
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        _ = await outputTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{tool} 执行失败：{Truncate(error, 1000)}");
    }

    private static string NormalizeAttachmentPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        const string prefix = "files/uploads/";
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal) || normalized[prefix.Length..].Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("附件路径不符合 files/uploads 契约。");
        return normalized[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
    }

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("文件路径逃逸允许目录。");
        return target;
    }

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
    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private sealed record AttachmentBackupEntry(string RelativePath, bool Existed);
}
