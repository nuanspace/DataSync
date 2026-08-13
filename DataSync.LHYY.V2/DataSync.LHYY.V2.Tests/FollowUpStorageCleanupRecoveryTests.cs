using DataSync.LHYY.V2.Models.FollowUp;
using DataSync.LHYY.V2.Services.FollowUp;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class FollowUpStorageCleanupRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"followup-cleanup-{Guid.NewGuid():N}");

    [Fact]
    public async Task Manifest_is_durable_before_database_transition_and_round_trips_candidate()
    {
        Directory.CreateDirectory(_root);
        var store = new FollowUpStorageCleanupManifestStore(_root);
        var packagePath = Path.Combine(_root, "package-1.fupkg");
        var candidate = new FollowUpStorageCleanupCandidate(
            "H001", "package-1", 10, new string('a', 64), packagePath, []);
        var manifest = new FollowUpStorageCleanupManifest
        {
            HospitalCode = "H001",
            PackageId = "package-1",
            Phase = FollowUpStorageCleanupPhase.Requested,
            Candidate = candidate,
            Items =
            [
                new FollowUpStorageCleanupManifestItem
                {
                    OriginalPath = packagePath,
                    QuarantinePath = packagePath + ".cleanup-op",
                    IsDirectory = false
                }
            ]
        };

        await store.WriteAsync(manifest, CancellationToken.None);
        var restored = await store.ReadAsync("H001", "package-1", CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(FollowUpStorageCleanupPhase.Requested, restored.Phase);
        Assert.Equal(candidate.PackageHash, restored.Candidate!.PackageHash);
        Assert.Equal(manifest.Items[0].QuarantinePath, restored.Items[0].QuarantinePath);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, ".cleanup-operations"), "*.tmp"));
    }

    [Fact]
    public async Task ReadAllAsync遇到坏清单时保留坏文件并继续返回好清单()
    {
        Directory.CreateDirectory(_root);
        var store = new FollowUpStorageCleanupManifestStore(_root);
        var good = new FollowUpStorageCleanupManifest
        {
            HospitalCode = "H001",
            PackageId = "good-package"
        };
        await store.WriteAsync(good, CancellationToken.None);
        var badPath = Path.Combine(_root, ".cleanup-operations", "broken.json");
        await File.WriteAllTextAsync(badPath, "{broken-json");
        var nullPath = Path.Combine(_root, ".cleanup-operations", "null.json");
        await File.WriteAllTextAsync(nullPath, "null");

        var manifests = await store.ReadAllAsync(CancellationToken.None);

        Assert.Equal("good-package", Assert.Single(manifests).PackageId);
        Assert.Equal("{broken-json", await File.ReadAllTextAsync(badPath));
        Assert.Equal("null", await File.ReadAllTextAsync(nullPath));
    }

    [Fact]
    public async Task ReadAllAsync隔离坏清单时记录包含文件路径的诊断日志()
    {
        Directory.CreateDirectory(_root);
        var logger = new RecordingLogger<FollowUpStorageCleanupManifestStore>();
        var constructor = typeof(FollowUpStorageCleanupManifestStore).GetConstructor(
            [typeof(string), typeof(ILogger<FollowUpStorageCleanupManifestStore>)]);
        Assert.NotNull(constructor);
        var store = Assert.IsType<FollowUpStorageCleanupManifestStore>(constructor!.Invoke([_root, logger]));
        var badPath = Path.Combine(_root, ".cleanup-operations", "broken.json");
        Directory.CreateDirectory(Path.GetDirectoryName(badPath)!);
        await File.WriteAllTextAsync(badPath, string.Empty);

        Assert.Empty(await store.ReadAllAsync(CancellationToken.None));
        Assert.Contains(logger.Messages, message => message.Contains(badPath, StringComparison.Ordinal));
        Assert.True(File.Exists(badPath));
    }

    [Fact]
    public void Crash_after_file_move_before_commit_restores_original_from_manifest()
    {
        Directory.CreateDirectory(_root);
        var original = Path.Combine(_root, "package-1.fupkg");
        var quarantine = original + ".cleanup-op";
        File.WriteAllText(original, "payload");
        File.Move(original, quarantine);
        var items = new[] { Item(original, quarantine) };

        Assert.Equal(FollowUpStorageCleanupRecoveryAction.RestoreFilesAndCancelDatabase,
            FollowUpStorageCleanupRecoveryPolicy.Decide(FollowUpStorageCleanupDatabaseState.Prepared));
        Assert.Empty(FollowUpStorageCleanupFileRecovery.Restore(items));
        Assert.True(File.Exists(original));
        Assert.False(File.Exists(quarantine));
        Assert.Equal("payload", File.ReadAllText(original));
    }

    [Fact]
    public void Uncertain_commit_that_database_reports_archived_deletes_quarantine_without_resurrection()
    {
        Directory.CreateDirectory(_root);
        var original = Path.Combine(_root, "package-1.fupkg");
        var quarantine = original + ".cleanup-op";
        File.WriteAllText(quarantine, "payload");
        var items = new[] { Item(original, quarantine) };

        Assert.Equal(FollowUpStorageCleanupRecoveryAction.DeleteQuarantine,
            FollowUpStorageCleanupRecoveryPolicy.Decide(FollowUpStorageCleanupDatabaseState.Archived));
        Assert.Empty(FollowUpStorageCleanupFileRecovery.DeleteQuarantine(items));
        Assert.False(File.Exists(original));
        Assert.False(File.Exists(quarantine));
    }

    [Fact]
    public void Archived清理发现隔离项已回到规范原路径时必须保留清单线索并阻断完成()
    {
        Directory.CreateDirectory(_root);
        var original = Path.Combine(_root, "package-1.fupkg");
        var quarantine = original + ".cleanup-op";
        File.WriteAllText(original, "unexpected-resurrection");
        var items = new[] { Item(original, quarantine) };

        var residue = FollowUpStorageCleanupFileRecovery.DeleteQuarantine(items);

        Assert.Equal(original, Assert.Single(residue));
        Assert.True(File.Exists(original));
        Assert.False(File.Exists(quarantine));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Archived清理发现隔离路径被替换为相反类型时不得误报删除完成(bool expectedDirectory)
    {
        Directory.CreateDirectory(_root);
        var original = Path.Combine(_root, expectedDirectory ? "backup" : "package-1.fupkg");
        var quarantine = original + ".cleanup-op";
        if (expectedDirectory)
            File.WriteAllText(quarantine, "unexpected-file");
        else
            Directory.CreateDirectory(quarantine);
        var items = new[]
        {
            new FollowUpStorageCleanupManifestItem
            {
                OriginalPath = original,
                QuarantinePath = quarantine,
                IsDirectory = expectedDirectory
            }
        };

        var residue = FollowUpStorageCleanupFileRecovery.DeleteQuarantine(items);

        Assert.Equal(quarantine, Assert.Single(residue));
        Assert.True(File.Exists(quarantine) || Directory.Exists(quarantine));
    }

    [Fact]
    public void Inconsistent_database_state_stops_instead_of_guessing()
    {
        Assert.Equal(FollowUpStorageCleanupRecoveryAction.StopForManualReview,
            FollowUpStorageCleanupRecoveryPolicy.Decide(FollowUpStorageCleanupDatabaseState.Inconsistent));
    }

    [Fact]
    public void Cas_requires_exact_affected_row_count()
    {
        FollowUpStorageCleanupCas.EnsureAffected("Pulled->Archiving", 1, 1);
        var error = Assert.Throws<InvalidOperationException>(() =>
            FollowUpStorageCleanupCas.EnsureAffected("Pulled->Archiving", 0, 1));
        Assert.Contains("事务已回滚", error.Message);
    }

    private static FollowUpStorageCleanupManifestItem Item(string original, string quarantine) => new()
    {
        OriginalPath = original,
        QuarantinePath = quarantine,
        IsDirectory = false
    };

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
