using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DataSync.CYYY.Data;
using DataSync.CYYY.Models;
using Microsoft.EntityFrameworkCore;

namespace DataSync.CYYY.Services;

/// <summary>
/// 动态接口平台客户端。
/// </summary>
public class DynamicApiClient
{
    private const int QueryTimeoutSeconds = 120;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDbContextFactory<SyncDbContext> _dbFactory;
    private readonly ILogger<DynamicApiClient> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly SemaphoreSlim _configLock = new(1, 1);
    private readonly SemaphoreSlim _requestIntervalLock = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
    private DynamicApiConfig? _cachedConfig;
    private DateTime _lastRequestAt = DateTime.MinValue;

    public DynamicApiClient(
        IHttpClientFactory httpClientFactory,
        IDbContextFactory<SyncDbContext> dbFactory,
        ILogger<DynamicApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public void ReloadConfig()
    {
        _cachedConfig = null;
        InvalidateToken();
    }

    public async Task<bool> HasConfigAsync(CancellationToken ct = default)
    {
        if (_cachedConfig != null)
            return true;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.DynamicApiConfigs.AnyAsync(ct);
    }

    public async Task TestConnectionAsync(DynamicApiConfig config, CancellationToken ct)
    {
        await RequestTokenAsync(config, ct);
    }

    public async Task<List<Dictionary<string, object>>> QueryAllPagesAsync(
        string queryPath,
        string patientId,
        string visitId,
        bool useTodayTimeRange,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(queryPath))
            throw new InvalidOperationException("动态接口查询路径不能为空");
        if (string.IsNullOrWhiteSpace(patientId))
            throw new InvalidOperationException("患者 ID 不能为空");
        if (string.IsNullOrWhiteSpace(visitId))
            throw new InvalidOperationException("住院次数不能为空");

        var config = await GetConfigAsync(ct);
        var endpoint = $"{config.QueryEndpointPrefix.TrimEnd('/')}/{queryPath.Trim('/')}";
        var today = DateTime.Today;
        var allData = new List<Dictionary<string, object>>();
        var pageNum = 1;

        while (true)
        {
            var request = new DynamicApiQueryRequest
            {
                PatientId = patientId,
                VisitId = visitId,
                StartTime = useTodayTimeRange ? today.ToString("yyyy-MM-dd 00:00:00") : null,
                EndTime = useTodayTimeRange ? today.ToString("yyyy-MM-dd 23:59:59") : null,
                PageNum = pageNum,
                PageSize = config.PageSize
            };

            var result = await ExecuteQueryAsync(config, endpoint, request, ct);
            allData.AddRange(ReadData(result.Data));

            var pagination = result.Pagination;
            if (pagination is null || !pagination.HasMore ||
                pagination.TotalPages > 0 && pageNum >= pagination.TotalPages)
            {
                break;
            }

            pageNum++;
        }

        return allData;
    }

    /// <summary>
    /// 按时间范围查询采集源，仅发送 startTime 和 endTime，不发送患者字段或分页字段。
    /// </summary>
    public async Task<List<Dictionary<string, object>>> QueryByTimeRangeAsync(
        string queryPath,
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(queryPath))
            throw new InvalidOperationException("动态接口采集查询路径不能为空");
        if (from > to)
            throw new InvalidOperationException("动态接口采集开始时间不能晚于结束时间");

        var config = await GetConfigAsync(ct);
        var endpoint = $"{config.QueryEndpointPrefix.TrimEnd('/')}/{queryPath.Trim('/')}";
        var request = new DynamicApiTimeRangeQueryRequest
        {
            StartTime = from.ToString("yyyy-MM-dd HH:mm:ss"),
            EndTime = to.ToString("yyyy-MM-dd HH:mm:ss")
        };
        var result = await ExecuteQueryAsync(config, endpoint, request, ct);
        return ReadData(result.Data);
    }

    private async Task<DynamicApiResponse<JsonElement>> ExecuteQueryAsync<TRequest>(
        DynamicApiConfig config,
        string endpoint,
        TRequest request,
        CancellationToken ct)
        where TRequest : class
    {
        using var response = await SendQueryWithRetryAsync(config, endpoint, request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"动态接口请求失败（{(int)response.StatusCode}）");

        var result = await response.Content.ReadFromJsonAsync<DynamicApiResponse<JsonElement>>(cancellationToken: ct)
            ?? throw new InvalidOperationException("动态接口响应解析失败");
        if (result.Code != 0)
            throw new InvalidOperationException($"动态接口查询失败：{result.Message}");

        return result;
    }

    private async Task<HttpResponseMessage> SendQueryWithRetryAsync<TRequest>(
        DynamicApiConfig config,
        string endpoint,
        TRequest request,
        CancellationToken ct)
        where TRequest : class
    {
        var response = await SendQueryAsync(config, endpoint, request, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        InvalidateToken();
        _logger.LogWarning("动态接口返回 401，刷新 Token 后重试一次");
        return await SendQueryAsync(config, endpoint, request, ct);
    }

    private async Task<HttpResponseMessage> SendQueryAsync<TRequest>(
        DynamicApiConfig config,
        string endpoint,
        TRequest request,
        CancellationToken ct)
        where TRequest : class
    {
        var token = await GetTokenAsync(ct);
        var client = CreateClient(config);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await WaitRequestIntervalAsync(config, ct);
        return await client.PostAsJsonAsync(endpoint, request, ct);
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiry)
            return _cachedToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiry)
                return _cachedToken;

            var config = await GetConfigAsync(ct);
            var tokenData = await RequestTokenAsync(config, ct);
            var expiry = DateTimeOffset.FromUnixTimeSeconds(tokenData.ExpireAt).AddMinutes(-1);
            if (expiry <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("动态接口 Token 已过期");

            _cachedToken = tokenData.Token;
            _tokenExpiry = expiry;
            _logger.LogInformation("动态接口 Token 已刷新，有效期至 {Expiry}", _tokenExpiry);
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<DynamicApiTokenData> RequestTokenAsync(DynamicApiConfig config, CancellationToken ct)
    {
        var client = CreateClient(config);
        await WaitRequestIntervalAsync(config, ct);
        using var response = await client.PostAsJsonAsync(config.TokenEndpoint, new
        {
            appKey = config.AppKey,
            appSecret = config.AppSecret
        }, ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"动态接口 Token 请求失败（{(int)response.StatusCode}）");

        var result = await response.Content.ReadFromJsonAsync<DynamicApiResponse<DynamicApiTokenData>>(cancellationToken: ct)
            ?? throw new InvalidOperationException("动态接口 Token 响应解析失败");
        if (result.Code != 0 || result.Data is null || string.IsNullOrWhiteSpace(result.Data.Token))
            throw new InvalidOperationException($"获取动态接口 Token 失败：{result.Message}");

        return result.Data;
    }

    private async Task<DynamicApiConfig> GetConfigAsync(CancellationToken ct)
    {
        if (_cachedConfig != null)
            return _cachedConfig;

        await _configLock.WaitAsync(ct);
        try
        {
            if (_cachedConfig != null)
                return _cachedConfig;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            _cachedConfig = await db.DynamicApiConfigs.FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("动态接口平台未配置");
            return _cachedConfig;
        }
        finally
        {
            _configLock.Release();
        }
    }

    private async Task WaitRequestIntervalAsync(DynamicApiConfig config, CancellationToken ct)
    {
        var intervalMs = Math.Max(0, config.RequestIntervalMilliseconds);
        if (intervalMs == 0)
            return;

        await _requestIntervalLock.WaitAsync(ct);
        try
        {
            var interval = TimeSpan.FromMilliseconds(intervalMs);
            var waitTime = interval - (DateTime.UtcNow - _lastRequestAt);
            if (waitTime > TimeSpan.Zero)
                await Task.Delay(waitTime, ct);

            _lastRequestAt = DateTime.UtcNow;
        }
        finally
        {
            _requestIntervalLock.Release();
        }
    }

    private HttpClient CreateClient(DynamicApiConfig config)
    {
        var client = _httpClientFactory.CreateClient("DynamicApi");
        client.BaseAddress = new Uri(config.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(QueryTimeoutSeconds);
        return client;
    }

    private static List<Dictionary<string, object>> ReadData(JsonElement data)
    {
        if (data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return [];

        if (data.ValueKind == JsonValueKind.Object)
        {
            var item = JsonSerializer.Deserialize<Dictionary<string, object>>(data.GetRawText());
            return item is null ? [] : [item];
        }

        if (data.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<Dictionary<string, object>>>(data.GetRawText()) ?? [];
        }

        throw new InvalidOperationException("动态接口 data 不是对象或数组");
    }

    private void InvalidateToken()
    {
        _cachedToken = null;
        _tokenExpiry = DateTimeOffset.MinValue;
    }
}
