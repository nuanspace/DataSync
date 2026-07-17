using DataSync.LHYY.V2.Services.FollowUp;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class FollowUpCubeOperationCoordinatorTests
{
    [Fact]
    public async Task 独占租约期间拒绝其他独占和共享操作()
    {
        var provider = new FakeAdvisoryLockProvider();
        var coordinator = new FollowUpCubeOperationCoordinator(provider);

        await using var exclusive = await coordinator.TryAcquireExclusiveAsync(CancellationToken.None);

        Assert.NotNull(exclusive);
        Assert.True(coordinator.IsMaintenanceActive);
        Assert.Null(await coordinator.TryAcquireExclusiveAsync(CancellationToken.None));
        Assert.Null(await coordinator.TryAcquireSharedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task 独占租约释放后自动恢复共享操作()
    {
        var provider = new FakeAdvisoryLockProvider();
        var coordinator = new FollowUpCubeOperationCoordinator(provider);
        var exclusive = await coordinator.TryAcquireExclusiveAsync(CancellationToken.None);

        await exclusive!.DisposeAsync();

        Assert.False(coordinator.IsMaintenanceActive);
        await using var shared = await coordinator.TryAcquireSharedAsync(CancellationToken.None);
        Assert.NotNull(shared);
        Assert.Equal(1, provider.ExclusiveReleaseCount);
    }

    [Fact]
    public async Task 同一进程并发共享操作复用一个数据库锁会话()
    {
        var provider = new FakeAdvisoryLockProvider();
        var coordinator = new FollowUpCubeOperationCoordinator(provider);

        var first = await coordinator.TryAcquireSharedAsync(CancellationToken.None);
        var second = await coordinator.TryAcquireSharedAsync(CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, provider.SharedAdmissionCount);
        Assert.Equal(1, provider.SharedAcquireCount);
        await first!.DisposeAsync();
        Assert.Equal(0, provider.SharedReleaseCount);
        await second!.DisposeAsync();
        Assert.Equal(1, provider.SharedReleaseCount);
    }

    [Fact]
    public async Task 并发首批共享操作不会耗尽锁连接池()
    {
        var coordinator = new FollowUpCubeOperationCoordinator(new CapacityAdvisoryLockProvider());
        var acquisitions = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(async () => await coordinator.TryAcquireSharedAsync(CancellationToken.None)))
            .ToArray();
        var all = Task.WhenAll(acquisitions);

        Assert.Same(all, await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(2))));
        foreach (var lease in await all)
        {
            Assert.NotNull(lease);
            await lease!.DisposeAsync();
        }
    }

    private sealed class FakeAdvisoryLockProvider : IFollowUpCubeAdvisoryLockProvider
    {
        public int ExclusiveReleaseCount { get; private set; }
        public int SharedAcquireCount { get; private set; }
        public int SharedAdmissionCount { get; private set; }
        public int SharedReleaseCount { get; private set; }

        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(new CallbackLease(() => ExclusiveReleaseCount++));

        public ValueTask<IAsyncDisposable?> TryAcquireSharedAdmissionAsync(CancellationToken cancellationToken)
        {
            SharedAdmissionCount++;
            return ValueTask.FromResult<IAsyncDisposable?>(new CallbackLease(() => { }));
        }

        public ValueTask<IAsyncDisposable?> TryAcquireSharedAsync(CancellationToken cancellationToken)
        {
            SharedAcquireCount++;
            return ValueTask.FromResult<IAsyncDisposable?>(new CallbackLease(() => SharedReleaseCount++));
        }
    }

    private sealed class CallbackLease(Action onDispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapacityAdvisoryLockProvider : IFollowUpCubeAdvisoryLockProvider
    {
        private readonly SemaphoreSlim _connections = new(2, 2);
        private readonly TaskCompletionSource _secondAdmission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _admissionCount;

        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(new CallbackLease(() => { }));

        public async ValueTask<IAsyncDisposable?> TryAcquireSharedAdmissionAsync(CancellationToken cancellationToken)
        {
            await _connections.WaitAsync(cancellationToken);
            if (Interlocked.Increment(ref _admissionCount) == 1)
                await Task.WhenAny(_secondAdmission.Task, Task.Delay(200, cancellationToken));
            else
                _secondAdmission.TrySetResult();
            return new CallbackLease(() => _connections.Release());
        }

        public async ValueTask<IAsyncDisposable?> TryAcquireSharedAsync(CancellationToken cancellationToken)
        {
            await _connections.WaitAsync(cancellationToken);
            return new CallbackLease(() => _connections.Release());
        }
    }
}
