using System.Text;
using System.Text.Json;

namespace DataSync.CYYY.Services;

/// <summary>
/// API 推送实现（导管室等）
/// </summary>
public class ApiPushService : IPushService
{
    private const string JsonFieldSuffix = "__JSON";

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

        var normalizedData = NormalizeJsonFields(data, serverCode);
        var client = _httpClientFactory.CreateClient();
        var payload = new
        {
            serverCode,
            data = normalizedData
        };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync(pushTarget, content, ct);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("API 推送成功：{Target}，接口 {ServerCode}，{Count} 条",
            pushTarget, serverCode, data.Count);
    }

    private static List<Dictionary<string, object>> NormalizeJsonFields(
        IEnumerable<Dictionary<string, object>> data,
        string serverCode)
    {
        return data.Select(record =>
        {
            var normalized = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var (fieldName, value) in record)
            {
                var isJsonField = fieldName.EndsWith(JsonFieldSuffix, StringComparison.OrdinalIgnoreCase);
                var outputName = isJsonField ? fieldName[..^JsonFieldSuffix.Length] : fieldName;
                if (string.IsNullOrWhiteSpace(outputName))
                    throw new InvalidOperationException($"接口 [{serverCode}] 存在无效的 JSON 字段名 [{fieldName}]");

                var outputValue = isJsonField
                    ? ParseJsonValue(value, serverCode, fieldName)
                    : value;
                if (!normalized.TryAdd(outputName, outputValue))
                    throw new InvalidOperationException($"接口 [{serverCode}] 的输出字段 [{outputName}] 重复");
            }

            return normalized;
        }).ToList();
    }

    private static JsonElement ParseJsonValue(object? value, string serverCode, string fieldName)
    {
        var json = value switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            JsonElement element => element.GetRawText(),
            _ => value?.ToString()
        };
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException($"接口 [{serverCode}] 的 JSON 字段 [{fieldName}] 不能为空");

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not JsonValueKind.Array and not JsonValueKind.Object)
                throw new InvalidOperationException($"接口 [{serverCode}] 的 JSON 字段 [{fieldName}] 必须是数组或对象");

            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"接口 [{serverCode}] 的 JSON 字段 [{fieldName}] 格式无效", ex);
        }
    }
}
