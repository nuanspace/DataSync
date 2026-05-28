using Microsoft.Extensions.Configuration;
using Npgsql;

namespace DataSync.LHYY.V2.Tools;

internal static class ToolConnectionHelper
{
    public static string ResolveConnectionString(string? connectionName, string? scriptsPath = null)
    {
        var rootPath = ResolveRootPath(scriptsPath);
        var configuration = new ConfigurationBuilder()
            .SetBasePath(rootPath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var name = string.IsNullOrWhiteSpace(connectionName) ? "DataSyncDb" : connectionName!;
        var connectionString = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"未找到连接字符串：{name}");

        return connectionString;
    }

    public static string DescribeConnection(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return $"Host={builder.Host}; Port={builder.Port}; Database={builder.Database}; Username={builder.Username}";
    }

    public static string ResolveRootPath(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(configuredPath);

        var currentPath = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(currentPath, "appsettings.json")))
            return currentPath;

        var projectPath = Path.Combine(currentPath, "DataSync.LHYY.V2");
        if (File.Exists(Path.Combine(projectPath, "appsettings.json")))
            return projectPath;

        return AppContext.BaseDirectory;
    }
}
