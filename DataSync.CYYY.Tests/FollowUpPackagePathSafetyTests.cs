using DataSync.CYYY.Services.FollowUp;
using DataSync.Common.FollowUp;
using Xunit;

namespace DataSync.CYYY.Tests;

public sealed class FollowUpPackagePathSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"followup-pull-{Guid.NewGuid():N}");

    [Fact]
    public void Canonical_package_path_is_accepted()
    {
        Directory.CreateDirectory(_root);
        FollowUpPackageSyncService.ValidateCanonicalPackagePath(
            _root, "package-1", Path.Combine(_root, "package-1.fupkg"));
    }

    [Fact]
    public void Different_file_or_nested_path_is_rejected()
    {
        Directory.CreateDirectory(_root);
        Assert.Throws<InvalidDataException>(() => FollowUpPackageSyncService.ValidateCanonicalPackagePath(
            _root, "package-1", Path.Combine(_root, "package-2.fupkg")));
        Assert.Throws<InvalidDataException>(() => FollowUpPackageSyncService.ValidateCanonicalPackagePath(
            _root, "package-1", Path.Combine(_root, "nested", "package-1.fupkg")));
    }

    [Fact]
    public void Package_lock_key_is_stable_across_hospital_services()
    {
        Assert.Equal("followup-package:H001:package-1",
            FollowUpPackageLockKey.Create(" h001 ", " package-1 "));
    }

    [Fact]
    public void Package_lock_contention_disposes_unleased_connection()
    {
        var source = ReadRepositorySource();
        var dispose = source.IndexOf("await connection.DisposeAsync();", StringComparison.Ordinal);
        var nullReturn = source.IndexOf("return null;", dispose, StringComparison.Ordinal);

        Assert.True(dispose >= 0 && nullReturn > dispose);
    }

    private static string ReadRepositorySource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DataSync.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(
            directory!.FullName,
            "DataSync.CYYY",
            "Services",
            "FollowUp",
            "FollowUpPackageRepository.cs"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
