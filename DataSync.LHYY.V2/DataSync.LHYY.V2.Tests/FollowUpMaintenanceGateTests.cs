using DataSync.LHYY.V2.Controllers;
using DataSync.LHYY.V2.Services;
using DataSync.LHYY.V2.Services.FollowUp;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class FollowUpMaintenanceGateTests
{
    [Fact]
    public async Task 维护期间ESB返回可重试业务失败()
    {
        var coordinator = new FollowUpCubeOperationCoordinator(new FakeAdvisoryLockProvider());
        await using var exclusive = await coordinator.TryAcquireExclusiveAsync(CancellationToken.None);
        var controller = new EsbController(
            null!,
            NullLogger<EsbController>.Instance,
            new ConfigurationBuilder().Build(),
            coordinator)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.Receive(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var head = json.RootElement.GetProperty("Response").GetProperty("Head");
        Assert.Equal("300.1", head.GetProperty("AckCode").GetString());
        Assert.Equal("系统维护中，请稍后重试", head.GetProperty("AckMessage").GetString());
    }

    [Fact]
    public async Task 维护期间SOAP在访问配置和接收服务前返回可重试故障()
    {
        var coordinator = new FollowUpCubeOperationCoordinator(new FakeAdvisoryLockProvider());
        await using var exclusive = await coordinator.TryAcquireExclusiveAsync(CancellationToken.None);
        var service = new SoapWebServiceService(
            null!,
            null!,
            NullLogger<SoapWebServiceService>.Instance,
            coordinator);

        var result = await service.ProcessAsync("bioo", null, "<invalid />", CancellationToken.None);

        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Contains("系统维护中，请稍后重试", result.Content);
    }

    [Fact]
    public async Task 维护期间消息Worker不会创建服务Scope()
    {
        var coordinator = new FollowUpCubeOperationCoordinator(new FakeAdvisoryLockProvider());
        await using var exclusive = await coordinator.TryAcquireExclusiveAsync(CancellationToken.None);
        var scopeFactory = new TrackingScopeFactory();
        var worker = new MessageProcessingService(
            scopeFactory,
            new MessageProcessingNotifier(),
            NullLogger<MessageProcessingService>.Instance,
            coordinator);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await worker.StopAsync(CancellationToken.None);

        Assert.False(scopeFactory.WasCalled);
    }

    [Fact]
    public async Task 其他实例维护期间页面重试操作也会被拒绝()
    {
        var coordinator = new FollowUpCubeOperationCoordinator(new RejectingSharedLockProvider());
        var service = new MessageQueryService(null!, null!, null!, null!, coordinator);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RetryMessageAsync(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BatchRetryAsync());
    }

    private sealed class FakeAdvisoryLockProvider : IFollowUpCubeAdvisoryLockProvider
    {
        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(new EmptyLease());

        public ValueTask<IAsyncDisposable?> TryAcquireSharedAdmissionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(new EmptyLease());

        public ValueTask<IAsyncDisposable?> TryAcquireSharedAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(new EmptyLease());
    }

    private sealed class EmptyLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RejectingSharedLockProvider : IFollowUpCubeAdvisoryLockProvider
    {
        public ValueTask<IAsyncDisposable?> TryAcquireExclusiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(new EmptyLease());

        public ValueTask<IAsyncDisposable?> TryAcquireSharedAdmissionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(new EmptyLease());

        public ValueTask<IAsyncDisposable?> TryAcquireSharedAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(null);
    }

    private sealed class TrackingScopeFactory : IServiceScopeFactory
    {
        public bool WasCalled { get; private set; }

        public IServiceScope CreateScope()
        {
            WasCalled = true;
            throw new InvalidOperationException("维护期间不应创建 Scope。");
        }
    }
}
