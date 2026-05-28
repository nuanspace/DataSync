using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Diagnostics;
using System.Text;
using DataSync.LHYY.V2.Tools;

namespace DataSync.LHYY.V2.Services;

public sealed class DatabaseUpgradeService
{
    public const long MaxSqlFileUploadBytes = 200L * 1024 * 1024;

    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public DatabaseUpgradeService(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    public List<DatabaseConnectionOption> GetConnectionOptions() =>
        _configuration.GetSection("ConnectionStrings")
            .GetChildren()
            .Select(section => new DatabaseConnectionOption(
                section.Key,
                section.Value ?? "",
                DescribeConnection(section.Key, section.Value ?? "")))
            .Where(item => !string.IsNullOrWhiteSpace(item.ConnectionString))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<DatabaseUpgradeCheckResult> CheckAsync(string connectionName, CancellationToken cancellationToken = default)
    {
        var target = GetConnection(connectionName);
        var scripts = LoadScripts();
        await using var connection = new NpgsqlConnection(target.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        return new DatabaseUpgradeCheckResult(
            target,
            scripts.Count,
            scripts.Select(script => script.RelativePath).ToList());
    }

    public async Task<DatabaseUpgradeExecuteResult> ExecuteAsync(
        string connectionName,
        string? pgDumpPath,
        bool skipBackup,
        CancellationToken cancellationToken = default)
    {
        var target = GetConnection(connectionName);
        var scripts = LoadScripts();
        await using var connection = new NpgsqlConnection(target.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        if (scripts.Count == 0)
            return new DatabaseUpgradeExecuteResult(target, "", []);

        var backupFile = skipBackup
            ? "已手工备份，跳过自动备份"
            : await BackupDatabaseAsync(target.ConnectionString, pgDumpPath, cancellationToken);

        foreach (var script in scripts)
            await ExecuteScriptAsync(connection, script, cancellationToken);

        return new DatabaseUpgradeExecuteResult(
            target,
            backupFile,
            scripts.Select(script => script.RelativePath).ToList());
    }

    public async Task<string> SaveSqlFileAsync(
        string fileName,
        Stream source,
        CancellationToken cancellationToken = default)
    {
        if (!fileName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只允许上传 .sql 文件。");

        var safeFileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var directory = Path.Combine(_environment.ContentRootPath, "DatabaseSqlFiles");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}_{safeFileName}");
        await using var target = File.Create(path);
        await source.CopyToAsync(target, cancellationToken);
        return path;
    }

    public async Task CheckSqlFileAsync(
        string connectionName,
        string sqlFilePath,
        CancellationToken cancellationToken = default)
    {
        var target = GetConnection(connectionName);
        var path = ResolveSqlFilePath(sqlFilePath);
        if (!File.Exists(path))
            throw new FileNotFoundException("SQL 文件不存在。", path);

        await using var connection = new NpgsqlConnection(target.ConnectionString);
        await connection.OpenAsync(cancellationToken);
    }

    public async Task<DatabaseUpgradeExecuteResult> ExecuteSqlFileAsync(
        string connectionName,
        string sqlFilePath,
        string? pgDumpPath,
        bool skipBackup,
        CancellationToken cancellationToken = default)
    {
        var target = GetConnection(connectionName);
        var path = ResolveSqlFilePath(sqlFilePath);
        if (!File.Exists(path))
            throw new FileNotFoundException("SQL 文件不存在。", path);

        var backupFile = skipBackup
            ? "已手工备份，跳过自动备份"
            : await BackupDatabaseAsync(target.ConnectionString, pgDumpPath, cancellationToken);
        await ExecuteSqlFileByPsqlAsync(target.ConnectionString, path, pgDumpPath, cancellationToken);

        return new DatabaseUpgradeExecuteResult(target, backupFile, [path]);
    }

    private DatabaseConnectionOption GetConnection(string connectionName)
    {
        var target = GetConnectionOptions().FirstOrDefault(item =>
            string.Equals(item.Name, connectionName, StringComparison.OrdinalIgnoreCase));
        return target ?? throw new InvalidOperationException($"未找到连接字符串：{connectionName}");
    }

    private List<UpgradeScript> LoadScripts()
    {
        var rootPath = _environment.ContentRootPath;
        var scripts = new List<UpgradeScript>();
        AddScriptIfExists(scripts, rootPath, Path.Combine(rootPath, "init_database.sql"));

        var scriptsPath = Path.Combine(rootPath, "Scripts");
        if (!Directory.Exists(scriptsPath))
            return scripts;

        foreach (var path in Directory.GetFiles(scriptsPath, "*.sql", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            AddScriptIfExists(scripts, rootPath, path);
        }

        foreach (var path in Directory.GetFiles(scriptsPath, "*.sql", SearchOption.AllDirectories)
                     .Where(path => !string.Equals(Path.GetDirectoryName(path), scriptsPath, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => Path.GetRelativePath(scriptsPath, path), StringComparer.OrdinalIgnoreCase))
        {
            AddScriptIfExists(scripts, rootPath, path);
        }

        return scripts;
    }

    private static void AddScriptIfExists(List<UpgradeScript> scripts, string rootPath, string path)
    {
        if (!File.Exists(path))
            return;

        var text = File.ReadAllText(path, Encoding.UTF8);
        var relativePath = Path.GetRelativePath(rootPath, path).Replace('/', '\\');
        scripts.Add(new UpgradeScript(relativePath, text));
    }

    private static async Task ExecuteScriptAsync(
        NpgsqlConnection connection,
        UpgradeScript script,
        CancellationToken cancellationToken)
        => await SqlScriptExecutionHelper.ExecuteAsync(connection, script.Sql, cancellationToken);

    private async Task<string> BackupDatabaseAsync(
        string connectionString,
        string? configuredPgDumpPath,
        CancellationToken cancellationToken)
    {
        var pgDumpPath = ResolvePgDumpPath(configuredPgDumpPath)
            ?? throw new InvalidOperationException("未找到 pg_dump.exe，无法备份，升级已停止。");

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var (host, port) = ResolveHostAndPort(builder);
        var username = !string.IsNullOrWhiteSpace(builder.Username)
            ? builder.Username
            : throw new InvalidOperationException("连接字符串缺少 Username，无法执行备份。");
        var database = !string.IsNullOrWhiteSpace(builder.Database)
            ? builder.Database
            : throw new InvalidOperationException("连接字符串缺少 Database，无法执行备份。");
        var backupDirectory = Path.Combine(_environment.ContentRootPath, "DatabaseBackups");
        Directory.CreateDirectory(backupDirectory);

        var backupFile = Path.Combine(
            backupDirectory,
            $"{database}_{DateTime.Now:yyyyMMdd_HHmmss}.backup");

        var process = new Process();
        process.StartInfo.FileName = pgDumpPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.Environment["PGPASSWORD"] = builder.Password ?? "";
        process.StartInfo.ArgumentList.Add("--host");
        process.StartInfo.ArgumentList.Add(host);
        process.StartInfo.ArgumentList.Add("--port");
        process.StartInfo.ArgumentList.Add(port.ToString());
        process.StartInfo.ArgumentList.Add("--username");
        process.StartInfo.ArgumentList.Add(username);
        process.StartInfo.ArgumentList.Add("--dbname");
        process.StartInfo.ArgumentList.Add(database);
        process.StartInfo.ArgumentList.Add("--format");
        process.StartInfo.ArgumentList.Add("c");
        process.StartInfo.ArgumentList.Add("--file");
        process.StartInfo.ArgumentList.Add(backupFile);
        process.StartInfo.ArgumentList.Add("--no-password");

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"pg_dump 备份失败：{error}{output}");

        return backupFile;
    }

    private async Task ExecuteSqlFileByPsqlAsync(
        string connectionString,
        string sqlFilePath,
        string? configuredToolPath,
        CancellationToken cancellationToken)
    {
        var psqlPath = ResolvePsqlPath(configuredToolPath)
            ?? throw new InvalidOperationException("未找到 psql.exe，无法执行 SQL 文件。");

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var (host, port) = ResolveHostAndPort(builder);
        var username = !string.IsNullOrWhiteSpace(builder.Username)
            ? builder.Username
            : throw new InvalidOperationException("连接字符串缺少 Username，无法执行 SQL 文件。");
        var database = !string.IsNullOrWhiteSpace(builder.Database)
            ? builder.Database
            : throw new InvalidOperationException("连接字符串缺少 Database，无法执行 SQL 文件。");

        var process = new Process();
        process.StartInfo.FileName = psqlPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.Environment["PGPASSWORD"] = builder.Password ?? "";
        process.StartInfo.ArgumentList.Add("--host");
        process.StartInfo.ArgumentList.Add(host);
        process.StartInfo.ArgumentList.Add("--port");
        process.StartInfo.ArgumentList.Add(port.ToString());
        process.StartInfo.ArgumentList.Add("--username");
        process.StartInfo.ArgumentList.Add(username);
        process.StartInfo.ArgumentList.Add("--dbname");
        process.StartInfo.ArgumentList.Add(database);
        process.StartInfo.ArgumentList.Add("--no-password");
        process.StartInfo.ArgumentList.Add("--set");
        process.StartInfo.ArgumentList.Add("ON_ERROR_STOP=on");
        process.StartInfo.ArgumentList.Add("--file");
        process.StartInfo.ArgumentList.Add(sqlFilePath);

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"psql 执行失败：{error}{output}");
    }

    private static string? ResolvePgDumpPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath)
                && string.Equals(Path.GetFileName(configuredPath), "pg_dump.exe", StringComparison.OrdinalIgnoreCase))
            {
                return configuredPath;
            }

            var directory = File.Exists(configuredPath)
                ? Path.GetDirectoryName(configuredPath)
                : configuredPath;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        var executableName = OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump";
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), executableName);
            if (File.Exists(candidate))
                return candidate;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var postgresRoot = Path.Combine(programFiles, "PostgreSQL");
        if (!Directory.Exists(postgresRoot))
            return null;

        return Directory.GetFiles(postgresRoot, executableName, SearchOption.AllDirectories)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? ResolvePsqlPath(string? configuredToolPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredToolPath))
        {
            if (File.Exists(configuredToolPath)
                && string.Equals(Path.GetFileName(configuredToolPath), "psql.exe", StringComparison.OrdinalIgnoreCase))
            {
                return configuredToolPath;
            }

            var directory = File.Exists(configuredToolPath)
                ? Path.GetDirectoryName(configuredToolPath)
                : configuredToolPath;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "psql.exe" : "psql");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        var executableName = OperatingSystem.IsWindows() ? "psql.exe" : "psql";
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), executableName);
            if (File.Exists(candidate))
                return candidate;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var postgresRoot = Path.Combine(programFiles, "PostgreSQL");
        if (!Directory.Exists(postgresRoot))
            return null;

        return Directory.GetFiles(postgresRoot, executableName, SearchOption.AllDirectories)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private string ResolveSqlFilePath(string sqlFilePath)
    {
        if (string.IsNullOrWhiteSpace(sqlFilePath))
            throw new InvalidOperationException("请先选择或填写 SQL 文件。");

        var path = Path.IsPathRooted(sqlFilePath)
            ? sqlFilePath
            : Path.Combine(_environment.ContentRootPath, sqlFilePath);
        path = Path.GetFullPath(path);

        if (!path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只允许执行 .sql 文件。");

        return path;
    }

    private static string DescribeConnection(string name, string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            var (host, port) = ResolveHostAndPort(builder);
            return $"{name}（Host={host}; Port={port}; Database={builder.Database}; Username={builder.Username}）";
        }
        catch
        {
            return name;
        }
    }

    private static (string Host, int Port) ResolveHostAndPort(NpgsqlConnectionStringBuilder builder)
    {
        var host = string.IsNullOrWhiteSpace(builder.Host) ? "localhost" : builder.Host;
        var port = builder.Port > 0 ? builder.Port : 5432;
        var parts = host.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[1], out var parsedPort))
            return (parts[0], parsedPort);

        return (host, port);
    }

    private sealed record UpgradeScript(string RelativePath, string Sql);
}

public sealed record DatabaseConnectionOption(string Name, string ConnectionString, string DisplayName);

public sealed record DatabaseUpgradeCheckResult(
    DatabaseConnectionOption Connection,
    int TotalScriptCount,
    List<string> PendingScripts);

public sealed record DatabaseUpgradeExecuteResult(
    DatabaseConnectionOption Connection,
    string BackupFile,
    List<string> ExecutedScripts);
