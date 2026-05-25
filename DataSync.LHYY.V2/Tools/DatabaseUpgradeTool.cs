using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Diagnostics;
using System.Text;

namespace DataSync.LHYY.V2.Tools;

public static class DatabaseUpgradeTool
{
    private const string CommandName = "db-upgrade";

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        try
        {
            var options = ToolOptions.Parse(args.Skip(1).ToArray());
            var rootPath = ResolveRootPath(options.ScriptsPath);
            var configuration = BuildConfiguration(rootPath);
            var connectionStrings = LoadConnectionStrings(configuration);
            if (connectionStrings.Count == 0)
            {
                Console.WriteLine("未找到任何连接字符串，请先检查 appsettings.json。");
                return 1;
            }

            var target = SelectConnectionString(connectionStrings, options.ConnectionName);
            if (target is null)
                return 1;

            var scripts = LoadScripts(rootPath);
            if (scripts.Count == 0)
            {
                Console.WriteLine("未找到可执行 SQL 脚本。");
                return 1;
            }

            await using var connection = new NpgsqlConnection(target.Value);
            await connection.OpenAsync();

            Console.WriteLine($"已连接：{DescribeConnection(target.Name, target.Value)}");
            Console.WriteLine($"检查完成：共有 {scripts.Count} 个待执行脚本。");

            foreach (var script in scripts)
                Console.WriteLine($"- {script.RelativePath}");

            if (options.CheckOnly)
                return 0;

            Console.WriteLine();
            Console.Write("本工具将先备份数据库，再执行以上脚本。请输入“确认”继续：");
            if (!string.Equals(Console.ReadLine(), "确认", StringComparison.Ordinal))
            {
                Console.WriteLine("用户未确认，已取消。");
                return 2;
            }

            var backupFile = await BackupDatabaseAsync(target.Value, rootPath, options.PgDumpPath);
            Console.WriteLine($"备份完成：{backupFile}");

            foreach (var script in scripts)
            {
                Console.WriteLine($"执行脚本：{script.RelativePath}");
                await ExecuteScriptAsync(connection, script);
            }

            Console.WriteLine("数据库升级完成。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"数据库升级失败：{ex.Message}");
            return 1;
        }
    }

    private static IConfigurationRoot BuildConfiguration(string rootPath) =>
        new ConfigurationBuilder()
            .SetBasePath(rootPath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

    private static List<ConnectionStringItem> LoadConnectionStrings(IConfiguration configuration) =>
        configuration.GetSection("ConnectionStrings")
            .GetChildren()
            .Select(section => new ConnectionStringItem(section.Key, section.Value ?? ""))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static ConnectionStringItem? SelectConnectionString(List<ConnectionStringItem> connectionStrings, string? connectionName)
    {
        if (!string.IsNullOrWhiteSpace(connectionName))
        {
            var selected = connectionStrings.FirstOrDefault(item =>
                string.Equals(item.Name, connectionName, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
                return selected;

            Console.WriteLine($"未找到连接字符串：{connectionName}");
            return null;
        }

        Console.WriteLine("请选择要升级的数据库：");
        for (var i = 0; i < connectionStrings.Count; i++)
            Console.WriteLine($"{i + 1}. {DescribeConnection(connectionStrings[i].Name, connectionStrings[i].Value)}");

        Console.Write("请输入编号：");
        if (!int.TryParse(Console.ReadLine(), out var index) || index < 1 || index > connectionStrings.Count)
        {
            Console.WriteLine("编号无效，已停止。");
            return null;
        }

        return connectionStrings[index - 1];
    }

    private static List<UpgradeScript> LoadScripts(string rootPath)
    {
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
        scripts.Add(new UpgradeScript(relativePath, path, text));
    }

    private static async Task ExecuteScriptAsync(NpgsqlConnection connection, UpgradeScript script)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var command = new NpgsqlCommand(script.Sql, connection, transaction) { CommandTimeout = 0 })
            {
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task<string> BackupDatabaseAsync(string connectionString, string rootPath, string? configuredPgDumpPath)
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
        var backupDirectory = Path.Combine(rootPath, "DatabaseBackups");
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
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"pg_dump 备份失败：{error}{output}");

        return backupFile;
    }

    private static string? ResolvePgDumpPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

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

    private static (string Host, int Port) ResolveHostAndPort(NpgsqlConnectionStringBuilder builder)
    {
        var host = string.IsNullOrWhiteSpace(builder.Host) ? "localhost" : builder.Host;
        var port = builder.Port > 0 ? builder.Port : 5432;
        var parts = host.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[1], out var parsedPort))
            return (parts[0], parsedPort);

        return (host, port);
    }

    private static string ResolveRootPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(configuredPath);

        var currentPath = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(currentPath, "init_database.sql")) || Directory.Exists(Path.Combine(currentPath, "Scripts")))
            return currentPath;

        return AppContext.BaseDirectory;
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

    private sealed record ConnectionStringItem(string Name, string Value);
    private sealed record UpgradeScript(string RelativePath, string FullPath, string Sql);

    private sealed record ToolOptions(
        string? ConnectionName = null,
        string? PgDumpPath = null,
        string? ScriptsPath = null,
        bool CheckOnly = false)
    {
        public static ToolOptions Parse(string[] args)
        {
            var options = new ToolOptions();
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--check":
                        options = options with { CheckOnly = true };
                        break;
                    case "--connection":
                    case "-c":
                        options = options with { ConnectionName = ReadValue(args, ref i) };
                        break;
                    case "--pg-dump":
                        options = options with { PgDumpPath = ReadValue(args, ref i) };
                        break;
                    case "--scripts":
                        options = options with { ScriptsPath = ReadValue(args, ref i) };
                        break;
                }
            }

            return options;
        }

        private static string? ReadValue(string[] args, ref int index)
        {
            if (index + 1 >= args.Length)
                return null;

            index++;
            return args[index];
        }
    }
}
