using DataSync.Common.FollowUp;

namespace DataSync.CYYY.Tests;

public sealed class FollowUpStorageInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"followup-storage-{Guid.NewGuid():N}");

    public FollowUpStorageInspectorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Inspect_统计目录文件并规范阈值()
    {
        File.WriteAllBytes(Path.Combine(_root, "package.fupkg"), new byte[128]);

        var status = FollowUpStorageInspector.Inspect("测试包仓库", _root, 80, 90);

        Assert.True(status.Ready);
        Assert.Equal(128, status.DirectoryBytes);
        Assert.Equal(80, status.WarningUsedPercent);
        Assert.Equal(90, status.CriticalUsedPercent);
        Assert.True(status.TotalBytes > 0);
    }

    [Fact]
    public void ValidateManagedFile_拒绝目录越界和错误扩展名()
    {
        var managed = Path.Combine(_root, "managed");
        Directory.CreateDirectory(managed);
        var valid = Path.Combine(managed, "a.fupkg");
        File.WriteAllText(valid, "ok");

        Assert.Equal(Path.GetFullPath(valid), FollowUpStorageInspector.ValidateManagedFile(managed, valid, ".fupkg"));
        Assert.Throws<InvalidOperationException>(() =>
            FollowUpStorageInspector.ValidateManagedFile(managed, Path.Combine(_root, "outside.fupkg"), ".fupkg"));
        Assert.Throws<InvalidOperationException>(() =>
            FollowUpStorageInspector.ValidateManagedFile(managed, Path.Combine(managed, "a.txt"), ".fupkg"));
    }

    [Fact]
    public void ValidateManagedPath_拒绝现存和悬空的符号链接()
    {
        var managed = Path.Combine(_root, "managed-links");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(managed);
        Directory.CreateDirectory(outside);

        var directoryLink = Path.Combine(managed, "directory-link");
        Directory.CreateSymbolicLink(directoryLink, outside);
        Assert.Throws<InvalidOperationException>(() =>
            FollowUpStorageInspector.ValidateManagedDirectory(managed, directoryLink));

        var danglingLink = Path.Combine(managed, "dangling-link");
        Directory.CreateSymbolicLink(danglingLink, Path.Combine(_root, "missing-target"));
        Assert.Throws<InvalidOperationException>(() =>
            FollowUpStorageInspector.ValidateManagedDirectory(managed, danglingLink));

        var danglingFileLink = Path.Combine(managed, "dangling-file.fupkg");
        File.CreateSymbolicLink(danglingFileLink, Path.Combine(_root, "missing-target.fupkg"));
        Assert.Throws<InvalidOperationException>(() =>
            FollowUpStorageInspector.ValidateManagedFile(managed, danglingFileLink, ".fupkg"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
