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
        using var service = new DatabaseUpgradeService(CreateConfiguration(), new TestEnvironment(), coordinator);
        var executed = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunExclusiveUpgradeOperationAsync("CubeDb", () =>
            {
                executed = true;
                return Task.FromResult(true);
            }));

        Assert.False(executed);
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

    private static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CubeDb"] = "Host=localhost;Port=5432;Database=cube;Username=test",
                ["ConnectionStrings:DataSyncDb"] = "Host=localhost;Port=5432;Database=datasync;Username=test"
            })
            .Build();

    private sealed class RejectingExclusiveLockProvider : IFollowUpCubeAdvisoryLockProvider
    {
        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(null);

        public ValueTask<IAsyncDisposable?> TryAcquireSharedAdmissionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(null);

        public ValueTask<IAsyncDisposable?> TryAcquireSharedAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(null);
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
