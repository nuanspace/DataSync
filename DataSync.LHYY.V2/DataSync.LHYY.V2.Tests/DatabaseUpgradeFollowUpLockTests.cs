using DataSync.LHYY.V2.Services;
using DataSync.LHYY.V2.Services.FollowUp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class DatabaseUpgradeFollowUpLockTests
{
    [Fact]
    public async Task CubeDb升级在FollowUp维护中被拒绝()
    {
        var coordinator = new FollowUpCubeOperationCoordinator(new RejectingExclusiveLockProvider());
        using var service = new DatabaseUpgradeService(CreateConfiguration("fresh-cube"), new TestEnvironment(), coordinator);
        var executed = false;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunExclusiveUpgradeOperationAsync("CubeDb", () =>
            {
                executed = true;
                return Task.FromResult(true);
            }));

        Assert.False(executed);
        Assert.DoesNotContain("external-cube", exception.Message);
    }

    [Fact]
    public async Task 非CubeDb升级不申请FollowUp维护租约()
    {
        var coordinator = new FollowUpCubeOperationCoordinator(new RejectingExclusiveLockProvider());
        using var service = new DatabaseUpgradeService(CreateConfiguration(), new TestEnvironment(), coordinator);

        var result = await service.RunExclusiveUpgradeOperationAsync(
            "DataSyncDb",
            () => Task.FromResult("executed"));

        Assert.Equal("executed", result);
    }

    [Fact]
    public async Task ExternalCube模式服务端拒绝CubeDb升级但不影响DataSyncDb()
    {
        var coordinator = new FollowUpCubeOperationCoordinator(new AllowingExclusiveLockProvider());
        using var service = new DatabaseUpgradeService(
            CreateConfiguration("external-cube"),
            new TestEnvironment(),
            coordinator);
        var cubeExecuted = false;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunExclusiveUpgradeOperationAsync("CubeDb", () =>
            {
                cubeExecuted = true;
                return Task.FromResult(true);
            }));
        var dataSyncResult = await service.RunExclusiveUpgradeOperationAsync(
            "DataSyncDb",
            () => Task.FromResult("executed"));

        Assert.False(cubeExecuted);
        Assert.Contains("external-cube", exception.Message);
        Assert.True(service.IsUpgradeExecutionBlocked("CubeDb"));
        Assert.False(service.IsUpgradeExecutionBlocked("DataSyncDb"));
        Assert.Equal("executed", dataSyncResult);
    }

    private static IConfiguration CreateConfiguration(string? deploymentMode = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:CubeDb"] = "Host=localhost;Port=5432;Database=cube;Username=test",
            ["ConnectionStrings:DataSyncDb"] = "Host=localhost;Port=5432;Database=datasync;Username=test"
        };
        if (deploymentMode is not null)
            values["Deployment:Mode"] = deploymentMode;

        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            (values))
            .Build();
    }

    private sealed class RejectingExclusiveLockProvider : IFollowUpCubeAdvisoryLockProvider
    {
        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(null);

        public ValueTask<IAsyncDisposable?> TryAcquireSharedAdmissionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(null);

        public ValueTask<IAsyncDisposable?> TryAcquireSharedAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(null);
    }

    private sealed class AllowingExclusiveLockProvider : IFollowUpCubeAdvisoryLockProvider
    {
        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(new NoOpLease());

        public ValueTask<IAsyncDisposable?> TryAcquireSharedAdmissionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(new NoOpLease());

        public ValueTask<IAsyncDisposable?> TryAcquireSharedAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(new NoOpLease());
    }

    private sealed class NoOpLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DataSync.LHYY.V2.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public string EnvironmentName { get; set; } = "Development";
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
