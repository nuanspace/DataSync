using System.Text.Json;
using DataSync.Common.FollowUp;
using DataSync.LHYY.V2.Models.FollowUp;
using Microsoft.Extensions.Options;

namespace DataSync.LHYY.V2.Services.FollowUp;

internal sealed record FollowUpRestoreCompletionMarker(
    Guid RestoreId,
    string HospitalCode,
    string PackageId,
    Guid BackupRecordId,
    DateTimeOffset? RestoredAt,
    string? AuditError,
    string? RestoreError = null);

public sealed class FollowUpRestoreCompletionStore(
    IOptions<FollowUpPackageImportOptions> options,
    ILogger<FollowUpRestoreCompletionStore>? logger = null)
{
    private readonly string _root = Path.Combine(Path.GetFullPath(options.Value.BackupRoot), ".restore-reconciliation");

    internal async Task SaveAsync(FollowUpRestoreCompletionMarker marker, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var path = MarkerPath(marker.RestoreId);
        var temporaryPath = Path.Combine(_root, $".{marker.RestoreId:N}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, marker, FollowUpJson.Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    internal async Task<IReadOnlyList<FollowUpRestoreCompletionMarker>> ReadCompletedAsync(CancellationToken cancellationToken)
        => (await ReadAllAsync(cancellationToken))
            .Where(marker => marker.RestoredAt is not null)
            .ToList();

    internal async Task<IReadOnlyList<FollowUpRestoreCompletionMarker>> ReadAllAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root)) return [];
        var markers = new List<FollowUpRestoreCompletionMarker>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var marker = await JsonSerializer.DeserializeAsync<FollowUpRestoreCompletionMarker>(
                    stream, FollowUpJson.Options, cancellationToken);
                if (marker is not null) markers.Add(marker);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                logger?.LogError(ex, "读取 FollowUp 恢复完成补写标记失败：{MarkerPath}", path);
            }
        }
        return markers;
    }

    internal Task DeleteAsync(Guid restoreId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(MarkerPath(restoreId));
        return Task.CompletedTask;
    }

    private string MarkerPath(Guid restoreId) => Path.Combine(_root, $"{restoreId:N}.json");
}
