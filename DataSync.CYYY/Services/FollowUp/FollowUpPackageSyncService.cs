using DataSync.CYYY.Models.FollowUp;
using DataSync.Common.FollowUp;
using Microsoft.Extensions.Options;

namespace DataSync.CYYY.Services.FollowUp;

public sealed class FollowUpPackageSyncService(
    FollowUpPackageRepository repository,
    FollowUpPackageRelayClient relayClient,
    FollowUpPackagePullCoordinator pullCoordinator,
    IOptions<FollowUpPackageSyncOptions> options,
    ILogger<FollowUpPackageSyncService> logger)
{
    private readonly FollowUpPackageSyncOptions _options = options.Value;

    public async Task<FollowUpPackageSyncOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var missing = await repository.GetMissingTablesAsync(cancellationToken);
        if (missing.Count > 0) return new FollowUpPackageSyncOverview { MissingTables = missing };
        return new FollowUpPackageSyncOverview
        {
            TablesReady = true,
            Sources = await repository.GetSourcesAsync(false, cancellationToken),
            Packages = await repository.GetPackagesAsync(cancellationToken: cancellationToken),
            Acks = await repository.GetAcksAsync(false, cancellationToken: cancellationToken)
        };
    }

    public Task SaveSourceAsync(FollowUpPackageSourceConfig source, CancellationToken cancellationToken = default) =>
        repository.SaveSourceAsync(source, cancellationToken);

    public async Task<FollowUpOperationResult> HealthAsync(FollowUpPackageSourceConfig source, CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await relayClient.HealthAsync(source, cancellationToken);
            return new FollowUpOperationResult(true, "DMZ 与云端链路连通。");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FollowUp DMZ 健康检查失败。HospitalCode={HospitalCode}", source.HospitalCode);
            return new FollowUpOperationResult(false, ex.Message, ErrorCode(ex));
        }
    }

    public async Task<FollowUpOperationResult> SyncSourceAsync(
        FollowUpPackageSourceConfig source,
        bool repull,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await pullCoordinator.TryAcquireAsync(source.HospitalCode, cancellationToken);
        if (lease is null)
            return new FollowUpOperationResult(false, "该医院已有拉取任务正在执行。");
        try
        {
            var after = repull ? null : await repository.GetMaxSequenceNoAsync(source.HospitalCode, cancellationToken);
            var remotePackages = (await relayClient.ListAsync(source, after, cancellationToken))
                .OrderBy(item => item.SequenceNo)
                .ToList();
            foreach (var package in remotePackages)
                await repository.UpsertPackageSummaryAsync(source.HospitalCode, package, cancellationToken);
            var packages = MergePullCandidates(
                remotePackages,
                await repository.GetRetryPackagesAsync(source.HospitalCode, cancellationToken));

            var successCount = 0;
            foreach (var package in packages)
            {
                await repository.MarkPackageAsync(source.HospitalCode, package.PackageId, "Pulling", null, null, null, cancellationToken);
                try
                {
                    var path = await relayClient.PullAsync(source, package, cancellationToken);
                    await repository.MarkPackageAsync(source.HospitalCode, package.PackageId, "Pulled", path, null, null, cancellationToken);
                    await repository.AddLogAsync(source.HospitalCode, package.PackageId, "relay-pull", "Info", "数据包拉取完成", new { package.SequenceNo, package.SizeBytes }, cancellationToken);
                    successCount++;
                }
                catch (Exception ex)
                {
                    await repository.MarkPackageAsync(source.HospitalCode, package.PackageId, "Failed", null, ErrorCode(ex), ex.Message, cancellationToken);
                    await repository.AddLogAsync(source.HospitalCode, package.PackageId, "relay-pull", "Error", "数据包拉取失败", new { errorCode = ErrorCode(ex) }, cancellationToken);
                }
            }

            await ForwardAcksAsync(source, cancellationToken);
            return new FollowUpOperationResult(true, $"查询到 {packages.Count} 个包，成功拉取 {successCount} 个。");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FollowUp 包同步失败。HospitalCode={HospitalCode}", source.HospitalCode);
            return new FollowUpOperationResult(false, ex.Message, ErrorCode(ex));
        }
    }

    public async Task<FollowUpOperationResult> RepullAsync(
        FollowUpPackageSourceConfig source,
        string? packageId,
        DateTime? fromWatermark,
        DateTime? toWatermark,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId) && fromWatermark is null && toWatermark is null)
            return new FollowUpOperationResult(false, "请填写包号或重拉时间范围。");
        if (fromWatermark.HasValue && toWatermark.HasValue && fromWatermark > toWatermark)
            return new FollowUpOperationResult(false, "重拉开始时间不能晚于结束时间。");

        await using var lease = await pullCoordinator.TryAcquireAsync(source.HospitalCode, cancellationToken);
        if (lease is null)
            return new FollowUpOperationResult(false, "该医院已有拉取任务正在执行。");
        try
        {
            var packages = FilterRepullCandidates(
                await relayClient.ListAsync(source, null, cancellationToken),
                packageId,
                fromWatermark,
                toWatermark);
            foreach (var package in packages)
                await repository.UpsertPackageSummaryAsync(source.HospitalCode, package, cancellationToken);

            var successCount = 0;
            foreach (var package in packages)
            {
                await repository.MarkPackageAsync(source.HospitalCode, package.PackageId, "Pulling", null, null, null, cancellationToken);
                try
                {
                    var path = await relayClient.PullAsync(source, package, cancellationToken);
                    await repository.MarkPackageAsync(source.HospitalCode, package.PackageId, "Pulled", path, null, null, cancellationToken);
                    await repository.AddLogAsync(source.HospitalCode, package.PackageId, "relay-repull", "Info", "数据包重拉完成",
                        new { package.SequenceNo, package.SizeBytes, packageId, fromWatermark, toWatermark }, cancellationToken);
                    successCount++;
                }
                catch (Exception ex)
                {
                    await repository.MarkPackageAsync(source.HospitalCode, package.PackageId, "Failed", null, ErrorCode(ex), ex.Message, cancellationToken);
                    await repository.AddLogAsync(source.HospitalCode, package.PackageId, "relay-repull", "Error", "数据包重拉失败",
                        new { errorCode = ErrorCode(ex) }, cancellationToken);
                }
            }
            return packages.Count == 0
                ? new FollowUpOperationResult(false, "云端清单中未找到符合条件的数据包。", FollowUpErrorCodes.PackageNotFound)
                : new FollowUpOperationResult(true, $"匹配 {packages.Count} 个包，成功重拉 {successCount} 个。");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FollowUp 包重拉失败。HospitalCode={HospitalCode}", source.HospitalCode);
            return new FollowUpOperationResult(false, ex.Message, ErrorCode(ex));
        }
    }

    public static List<FollowUpPackageSummary> FilterRepullCandidates(
        IEnumerable<FollowUpPackageSummary> packages,
        string? packageId,
        DateTime? fromWatermark,
        DateTime? toWatermark) =>
        packages
            .Where(item => string.IsNullOrWhiteSpace(packageId)
                || string.Equals(item.PackageId, packageId.Trim(), StringComparison.Ordinal))
            .Where(item => !fromWatermark.HasValue
                || item.ToWatermark.HasValue && item.ToWatermark.Value >= fromWatermark.Value)
            .Where(item => !toWatermark.HasValue
                || item.FromWatermark.HasValue && item.FromWatermark.Value <= toWatermark.Value)
            .OrderBy(item => item.SequenceNo)
            .ToList();

    public static List<FollowUpPackageSummary> MergePullCandidates(
        IEnumerable<FollowUpPackageSummary> remotePackages,
        IEnumerable<FollowUpPackageSummary> retryPackages) =>
        remotePackages
            .Concat(retryPackages)
            .GroupBy(item => item.PackageId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.SequenceNo)
            .ToList();

    public async Task ForwardAcksAsync(FollowUpPackageSourceConfig source, CancellationToken cancellationToken = default)
    {
        var acks = (await repository.GetAcksAsync(true, cancellationToken: cancellationToken))
            .Where(item => string.Equals(item.HospitalCode, source.HospitalCode, StringComparison.OrdinalIgnoreCase));
        foreach (var ack in acks)
        {
            try
            {
                await relayClient.AckAsync(source, ack.AckPayloadJson, cancellationToken);
                await repository.MarkAckAsync(ack.Id, true, null, null, _options.AckRetrySeconds, cancellationToken);
            }
            catch (Exception ex)
            {
                await repository.MarkAckAsync(ack.Id, false, ErrorCode(ex), ex.Message, _options.AckRetrySeconds, cancellationToken);
            }
        }
    }

    private static string ErrorCode(Exception ex) => ex is FollowUpPackageException protocol
        ? protocol.ErrorCode
        : FollowUpErrorCodes.InternalError;
}
