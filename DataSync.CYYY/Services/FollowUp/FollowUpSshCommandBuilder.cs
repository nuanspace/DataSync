using DataSync.CYYY.Models.FollowUp;
using System.Diagnostics;

namespace DataSync.CYYY.Services.FollowUp;

public static class FollowUpSshCommandBuilder
{
    private static readonly HashSet<string> AllowedOperations =
    ["relay-health", "relay-list", "relay-pull", "relay-ack"];

    public static ProcessStartInfo Create(
        FollowUpPackageSyncOptions options,
        FollowUpPackageSourceConfig source,
        string operation)
    {
        if (!AllowedOperations.Contains(operation))
            throw new ArgumentOutOfRangeException(nameof(operation), "不支持的 DMZ relay 操作。");
        if (string.IsNullOrWhiteSpace(source.DmzHost)
            || string.IsNullOrWhiteSpace(source.DmzUser)
            || source.DmzPort is < 1 or > 65535
            || string.IsNullOrWhiteSpace(options.PrivateKeyPath)
            || string.IsNullOrWhiteSpace(options.KnownHostsPath))
        {
            throw new InvalidOperationException("DMZ SSH 配置不完整。");
        }

        var startInfo = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        Add(startInfo, "-T");
        Add(startInfo, "-p", source.DmzPort.ToString());
        Add(startInfo, "-i", options.PrivateKeyPath);
        Add(startInfo, "-o", "BatchMode=yes");
        Add(startInfo, "-o", "IdentitiesOnly=yes");
        Add(startInfo, "-o", "PasswordAuthentication=no");
        Add(startInfo, "-o", "StrictHostKeyChecking=yes");
        Add(startInfo, "-o", $"UserKnownHostsFile={options.KnownHostsPath}");
        Add(startInfo, "-o", $"ConnectTimeout={Math.Clamp(options.ConnectTimeoutSeconds, 1, 60)}");
        Add(startInfo, $"{source.DmzUser}@{source.DmzHost}");
        Add(startInfo, operation);
        return startInfo;
    }

    private static void Add(ProcessStartInfo startInfo, params string[] values)
    {
        foreach (var value in values) startInfo.ArgumentList.Add(value);
    }
}
