using DataSync.LHYY.V2.Models.FollowUp;
using DataSync.LHYY.V2.Services.FollowUp;
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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
