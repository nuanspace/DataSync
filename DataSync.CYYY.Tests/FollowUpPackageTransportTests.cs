using DataSync.CYYY.Models.FollowUp;
using DataSync.CYYY.Services.FollowUp;
using System.Security.Cryptography;

namespace DataSync.CYYY.Tests;

public sealed class FollowUpPackageTransportTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "datasync-followup-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SSH命令固定启用严格主机校验且参数不经过Shell()
    {
        var options = new FollowUpPackageSyncOptions
        {
            PrivateKeyPath = "/keys/id_ed25519",
            KnownHostsPath = "/keys/known_hosts",
            ConnectTimeoutSeconds = 12
        };
        var source = new FollowUpPackageSourceConfig
        {
            DmzHost = "dmz.example",
            DmzPort = 2222,
            DmzUser = "sync"
        };

        var startInfo = FollowUpSshCommandBuilder.Create(options, source, "relay-list");

        Assert.False(startInfo.UseShellExecute);
        Assert.Contains("StrictHostKeyChecking=yes", startInfo.ArgumentList);
        Assert.Contains("UserKnownHostsFile=/keys/known_hosts", startInfo.ArgumentList);
        Assert.Contains("relay-list", startInfo.ArgumentList);
        Assert.DoesNotContain(startInfo.ArgumentList, item => item.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 完整包校验通过后原子保存()
    {
        var bytes = "followup-package"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var store = new FollowUpPackageFileStore();

        var path = await store.SaveAsync(
            _tempRoot, "pkg-1", new MemoryStream(bytes), bytes.Length, hash, 1024, CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.DoesNotContain(Directory.EnumerateFiles(_tempRoot), item => item.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hash不匹配时删除半包()
    {
        var store = new FollowUpPackageFileStore();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
            _tempRoot, "pkg-2", new MemoryStream("broken"u8.ToArray()), 6, new string('0', 64), 1024, CancellationToken.None));

        Assert.Contains("SHA-256", exception.Message);
        Assert.Empty(Directory.Exists(_tempRoot) ? Directory.EnumerateFiles(_tempRoot) : []);
    }

    [Fact]
    public async Task 超过大小限制时拒绝且不保留文件()
    {
        var store = new FollowUpPackageFileStore();

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
            _tempRoot, "pkg-3", new MemoryStream(new byte[16]), 16, null, 8, CancellationToken.None));

        Assert.Empty(Directory.Exists(_tempRoot) ? Directory.EnumerateFiles(_tempRoot) : []);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
    }
}
