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

    [Fact]
    public async Task 持久危险状态阻断普通共享和独占操作但允许恢复操作()
    {
        var coordinator = new FollowUpCubeOperationCoordinator(
            new FakeAdvisoryLockProvider(),
            new FixedPersistentStateGate(true));

        Assert.Null(await coordinator.TryAcquireSharedAsync(CancellationToken.None));
        Assert.Null(await coordinator.TryAcquireExclusiveAsync(CancellationToken.None));
        await using var recovery = await coordinator.TryAcquireRecoveryExclusiveAsync(CancellationToken.None);
        Assert.NotNull(recovery);
    }

    [Fact]
    public async Task 无危险状态时保持原有共享和独占租约行为()
    {
        var coordinator = new FollowUpCubeOperationCoordinator(
            new FakeAdvisoryLockProvider(),
            new FixedPersistentStateGate(false));

        await using (var shared = await coordinator.TryAcquireSharedAsync(CancellationToken.None))
            Assert.NotNull(shared);
        await using var exclusive = await coordinator.TryAcquireExclusiveAsync(CancellationToken.None);
        Assert.NotNull(exclusive);
    }

    [Fact]
    public async Task 持久状态查询异常时释放已取得的独占锁()
    {
        var provider = new FakeAdvisoryLockProvider();
        var coordinator = new FollowUpCubeOperationCoordinator(provider, new ThrowingPersistentStateGate());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.TryAcquireExclusiveAsync(CancellationToken.None));

        Assert.Equal(1, provider.ExclusiveReleaseCount);
        Assert.False(coordinator.IsMaintenanceActive);
    }

    [Theory]
    [InlineData("42P01", true)]
    [InlineData("08006", false)]
    public void 仅在管理表尚未创建时保持原有写入行为(string sqlState, bool expected)
    {
        Assert.Equal(expected, PostgreSqlFollowUpCubePersistentStateGate.IsMissingStateTable(sqlState));
    }

    [Fact]
    public async Task 持久状态在有效期内复用且主动失效后立即重查()
    {
        var queryCount = 0;
        var timeProvider = new MutableTimeProvider();
        var cache = new FollowUpCubePersistentStateCache(
            _ =>
            {
                queryCount++;
                return ValueTask.FromResult(false);
            },
            timeProvider,
            TimeSpan.FromSeconds(1));

        Assert.False(await cache.IsBlockedAsync(CancellationToken.None));
        Assert.False(await cache.IsBlockedAsync(CancellationToken.None));
        Assert.Equal(1, queryCount);

        cache.Invalidate();

        Assert.False(await cache.IsBlockedAsync(CancellationToken.None));
        Assert.Equal(2, queryCount);
    }

    [Fact]
    public async Task 持久状态缓存到期后自动重查()
    {
        var queryCount = 0;
        var timeProvider = new MutableTimeProvider();
        var cache = new FollowUpCubePersistentStateCache(
            _ =>
            {
                queryCount++;
                return ValueTask.FromResult(false);
            },
            timeProvider,
            TimeSpan.FromSeconds(1));

        await cache.IsBlockedAsync(CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        await cache.IsBlockedAsync(CancellationToken.None);

        Assert.Equal(2, queryCount);
    }

    [Fact]
    public void 导入状态迁移主动失效持久状态缓存()
    {
        var source = ReadSource("DataSync.LHYY.V2", "Services", "FollowUp", "FollowUpPackageImportRepository.cs");

        Assert.Contains("operationCoordinator.InvalidatePersistentStateGate()", source);
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

    private sealed class FixedPersistentStateGate(bool blocked) : IFollowUpCubePersistentStateGate
    {
        public ValueTask<bool> IsBlockedAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(blocked);
    }

    private sealed class ThrowingPersistentStateGate : IFollowUpCubePersistentStateGate
    {
        public ValueTask<bool> IsBlockedAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<bool>(new InvalidOperationException("gate failed"));
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DataSync.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. segments]));
    }
}
