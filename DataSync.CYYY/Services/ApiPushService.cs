using System.Text;
using System.Text.Json;

namespace DataSync.CYYY.Services;

/// <summary>
/// API 推送实现（导管室等）
/// </summary>
public class ApiPushService : IPushService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ApiPushService> _logger;

    public ApiPushService(IHttpClientFactory httpClientFactory, ILogger<ApiPushService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task PushAsync(string pushTarget, string serverCode, List<Dictionary<string, object>> data, CancellationToken ct)
    {
        if (data.Count == 0) return;

        var client = _httpClientFactory.CreateClient();
        var payload = new
        {
            serverCode,
            data
        };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync(pushTarget, content, ct);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("API 推送成功：{Target}，接口 {ServerCode}，{Count} 条",
            pushTarget, serverCode, data.Count);
    }
}
