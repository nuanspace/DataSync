using DataSync.CYYY.Models.FollowUp;
using DataSync.CYYY.Services.FollowUp;
using DataSync.CYYY.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataSync.CYYY.Tests;

public sealed class FollowUpPackagePullCoordinatorTests
{
    [Fact]
    public async Task 同一医院不能同时获取两次拉取租约()
    {
        var coordinator = new FollowUpPackagePullCoordinator();

        await using var first = await coordinator.TryAcquireAsync("hospital-a", CancellationToken.None);
        await using var second = await coordinator.TryAcquireAsync("hospital-a", CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task 不同医院可以同时拉取且释放后可以重新获取()
    {
        var coordinator = new FollowUpPackagePullCoordinator();
        var first = await coordinator.TryAcquireAsync("hospital-a", CancellationToken.None);
        await using var other = await coordinator.TryAcquireAsync("hospital-b", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(other);
        await first!.DisposeAsync();
        await using var again = await coordinator.TryAcquireAsync("hospital-a", CancellationToken.None);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Worker关闭时不会创建服务Scope()
    {
        var scopeFactory = new TrackingScopeFactory();
        var worker = new FollowUpPackagePullWorker(
            scopeFactory,
            Options.Create(new FollowUpPackageSyncOptions { Enabled = false }),
            NullLogger<FollowUpPackagePullWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.False(scopeFactory.WasCalled);
    }

    private sealed class TrackingScopeFactory : IServiceScopeFactory
    {
        public bool WasCalled { get; private set; }

        public IServiceScope CreateScope()
        {
            WasCalled = true;
            throw new InvalidOperationException("Worker 关闭时不应创建 Scope。");
        }
    }
}
