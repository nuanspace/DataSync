using System.Net;
using System.Text;
using DataSync.CYYY.Models;
using DataSync.CYYY.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataSync.CYYY.Tests;

public sealed class ActiveMedicalRecordClientTests
{
    [Fact]
    public async Task GetActiveRecordsAsync_服务根地址自动补全接口路径()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":{\"items\":[]}}", Encoding.UTF8, "application/json")
        });
        var client = CreateClient(handler);

        var result = await client.GetActiveRecordsAsync(new ActiveSyncTask
        {
            ActiveRecordsUrl = "https://localhost:50145/",
            CaseBatchSize = 10
        }, CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal("/api/active-medical-records", handler.RequestUri?.AbsolutePath);
        Assert.Contains("limit=10", handler.RequestUri?.Query);
    }

    [Fact]
    public async Task GetActiveRecordsAsync_Html响应转换为明确配置错误()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!DOCTYPE html><html></html>", Encoding.UTF8, "text/html")
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetActiveRecordsAsync(new ActiveSyncTask
            {
                ActiveRecordsUrl = "https://localhost:50145/api/active-medical-records"
            }, CancellationToken.None));

        Assert.Contains("返回了 HTML", exception.Message);
        Assert.DoesNotContain("<!DOCTYPE", exception.Message);
    }

    private static ActiveMedicalRecordClient CreateClient(HttpMessageHandler handler) =>
        new(
            new TestHttpClientFactory(new HttpClient(handler)),
            NullLogger<ActiveMedicalRecordClient>.Instance);

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(responder(request));
        }
    }
}
