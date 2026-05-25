using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataSync.CYYY.Data;
using DataSync.CYYY.Models;
using Microsoft.EntityFrameworkCore;

namespace DataSync.CYYY.Services;

/// <summary>
/// 数据湖查询客户端。
/// </summary>
public class DataLakeClient
{
    private const int QueryTimeoutSeconds = 120;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDbContextFactory<SyncDbContext> _dbFactory;
    private readonly ILogger<DataLakeClient> _logger;

    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private DataLakeConfig? _cachedConfig;
    private readonly SemaphoreSlim _configLock = new(1, 1);
    private readonly SemaphoreSlim _requestIntervalLock = new(1, 1);
    private DateTime _lastRequestAt = DateTime.MinValue;

    public DataLakeClient(
        IHttpClientFactory httpClientFactory,
        IDbContextFactory<SyncDbContext> dbFactory,
        ILogger<DataLakeClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// 清空配置和 Token 缓存。
    /// </summary>
    public void ReloadConfig()
    {
        _cachedConfig = null;
        _cachedToken = null;
        _tokenExpiry = DateTime.MinValue;
        _logger.LogInformation("数据湖配置缓存已清空，下次请求将重新加载");
    }

    /// <summary>
    /// 检查是否已完成数据湖配置。
    /// </summary>
    public async Task<bool> HasConfigAsync(CancellationToken ct = default)
    {
        if (_cachedConfig != null)
            return true;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.DataLakeConfigs.AnyAsync(ct);
    }

    /// <summary>
    /// 测试配置是否可正常获取 Token。
    /// </summary>
    public async Task TestConnectionAsync(DataLakeConfig config, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("DataLake");
        client.BaseAddress = new Uri(config.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(QueryTimeoutSeconds);
        var tokenUrl = BuildRequestUrl(config.BaseUrl, config.TokenEndpoint);

        var tokenParams = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret
        };
        var content = new FormUrlEncodedContent(tokenParams);

        if (config.DebugLogEnabled)
        {
            _logger.LogInformation(
                "[数据湖调试] 测试连接请求：{Url}，参数：{Params}",
                tokenUrl,
                JsonSerializer.Serialize(tokenParams));
        }

        await WaitRequestIntervalAsync(config, ct);
        using var response = await client.PostAsync(config.TokenEndpoint, content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = json.Length > 500 ? json[..500] : json;
            throw new HttpRequestException(
                $"数据湖 Token 测试失败（{(int)response.StatusCode}）[{tokenUrl}]：{errorBody}");
        }

        if (config.DebugLogEnabled)
            _logger.LogInformation("[数据湖调试] 测试连接返回：{Response}", json);

        var tokenResp = JsonSerializer.Deserialize<TokenApiResponse>(json)
            ?? throw new InvalidOperationException("Token 响应解析失败");

        if (tokenResp.Code != 200 || tokenResp.Data is null || string.IsNullOrEmpty(tokenResp.Data.Token))
            throw new InvalidOperationException($"获取 Token 失败：{tokenResp.Message}");
    }

    /// <summary>
    /// 自动分页查询，并按页回调处理。
    /// </summary>
    public async Task<(int TotalCount, int PageCount)> QueryPagesAsync(
        string serverCode,
        List<DataLakeCondition> condition,
        Func<List<Dictionary<string, object>>, int, Task> onPageAsync,
        CancellationToken ct)
    {
        var config = await GetConfigAsync(ct);
        var totalCount = 0;
        var pageCount = 0;
        var warnedPossibleTruncate = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pageNum = pageCount + 1;

            var request = new DataLakeQueryRequest
            {
                SysCode = config.SysCode,
                ServerCode = serverCode,
                Condition = condition,
                PageNo = pageNum,
                PageSize = config.PageSize,
                MaxResultSize = config.MaxResultSize
            };

            var data = await ExecuteQueryAsync(request, ct);
            if (data == null || data.Count == 0)
            {
                if (pageNum == 1)
                    _logger.LogWarning("查询 {ServerCode} 返回空数据", serverCode);
                break;
            }

            pageCount = pageNum;
            totalCount += data.Count;
            await onPageAsync(data, pageNum);

            if (!warnedPossibleTruncate && totalCount >= config.MaxResultSize)
            {
                warnedPossibleTruncate = true;
                _logger.LogWarning(
                    "查询 {ServerCode} 已累计返回 {Count} 条，达到 MaxResultSize={MaxResultSize}；若数据湖按总量截断，结果可能不完整",
                    serverCode,
                    totalCount,
                    config.MaxResultSize);
            }

            if (data.Count < config.PageSize)
                break;
        }

        _logger.LogDebug("查询 {ServerCode} 完成，共 {PageCount} 页，{Count} 条", serverCode, pageCount, totalCount);
        return (totalCount, pageCount);
    }

    /// <summary>
    /// 自动分页查询所有数据，并聚合返回。
    /// </summary>
    public async Task<List<Dictionary<string, object>>> QueryAllPagesAsync(
        string serverCode,
        List<DataLakeCondition> condition,
        CancellationToken ct)
    {
        var allData = new List<Dictionary<string, object>>();
        await QueryPagesAsync(serverCode, condition, (pageData, _) =>
        {
            allData.AddRange(pageData);
            return Task.CompletedTask;
        }, ct);
        return allData;
    }

    /// <summary>
    /// 按时间范围查询数据。
    /// </summary>
    public async Task<List<Dictionary<string, object>>> QueryByTimeRangeAsync(
        string serverCode,
        string timeField,
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        var condition = new List<DataLakeCondition>
        {
            new() { Column = timeField, Type = "ge", Value = from.ToString("yyyy-MM-dd HH:mm:ss") },
            new() { Column = timeField, Type = "le", Value = to.ToString("yyyy-MM-dd HH:mm:ss") }
        };
        return await QueryAllPagesAsync(serverCode, condition, ct);
    }

    /// <summary>
    /// 按字段值查询数据。
    /// </summary>
    public async Task<List<Dictionary<string, object>>> QueryByFieldAsync(
        string serverCode,
        string fieldName,
        List<string> values,
        CancellationToken ct)
    {
        List<DataLakeCondition> condition;
        if (values.Count == 1)
        {
            condition = [new() { Column = fieldName, Type = "eq", Value = values[0] }];
        }
        else
        {
            condition = [new() { Column = fieldName, Type = "in", Value = string.Join(",", values) }];
        }

        return await QueryAllPagesAsync(serverCode, condition, ct);
    }

    /// <summary>
    /// 获取当前配置。
    /// </summary>
    private async Task<DataLakeConfig> GetConfigAsync(CancellationToken ct)
    {
        if (_cachedConfig != null)
            return _cachedConfig;

        await _configLock.WaitAsync(ct);
        try
        {
            if (_cachedConfig != null)
                return _cachedConfig;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            _cachedConfig = await db.DataLakeConfigs.FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("数据湖未配置，请先在“数据湖配置”页面完成配置");

            _logger.LogDebug("数据湖配置已从数据库加载");
            return _cachedConfig;
        }
        finally
        {
            _configLock.Release();
        }
    }

    /// <summary>
    /// 获取并缓存 Token。
    /// </summary>
    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_cachedToken != null && DateTime.Now < _tokenExpiry)
            return _cachedToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken != null && DateTime.Now < _tokenExpiry)
                return _cachedToken;

            var config = await GetConfigAsync(ct);
            var client = CreateClient(config);
            var tokenUrl = BuildRequestUrl(config.BaseUrl, config.TokenEndpoint);
            var tokenParams = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = config.ClientId,
                ["client_secret"] = config.ClientSecret
            };
            var content = new FormUrlEncodedContent(tokenParams);

            if (config.DebugLogEnabled)
            {
                _logger.LogInformation(
                    "[数据湖调试] Token 请求：{Url}，参数：{Params}",
                    tokenUrl,
                    JsonSerializer.Serialize(tokenParams));
            }

            await WaitRequestIntervalAsync(config, ct);
            using var response = await client.PostAsync(config.TokenEndpoint, content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = json.Length > 500 ? json[..500] : json;
                throw new HttpRequestException(
                    $"数据湖 Token 请求失败（{(int)response.StatusCode}）[{tokenUrl}]：{errorBody}");
            }

            if (config.DebugLogEnabled)
                _logger.LogInformation("[数据湖调试] Token 返回：{Response}", json);

            var tokenResp = JsonSerializer.Deserialize<TokenApiResponse>(json)
                ?? throw new InvalidOperationException("Token 响应解析失败");

            if (tokenResp.Code != 200 || tokenResp.Data is null || string.IsNullOrEmpty(tokenResp.Data.Token))
                throw new InvalidOperationException($"获取 Token 失败：{tokenResp.Message}");

            _cachedToken = tokenResp.Data.Token;
            _tokenExpiry = DateTime.Now.AddSeconds(tokenResp.Data.ExpiresIn - 60);
            _logger.LogInformation("数据湖 Token 已刷新，有效期至 {Expiry}", _tokenExpiry);
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<List<Dictionary<string, object>>?> ExecuteQueryAsync(
        DataLakeQueryRequest request,
        CancellationToken ct)
    {
        var config = await GetConfigAsync(ct);
        var json = JsonSerializer.Serialize(request);

        using var response = await SendQueryWithRetryAsync(config, json, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        if (config.DebugLogEnabled)
        {
            var display = responseJson.Length > 2000
                ? $"{responseJson[..2000]}...（共 {responseJson.Length} 字符）"
                : responseJson;
            _logger.LogInformation(
                "[数据湖调试] 返回数据（状态码 {StatusCode}）：{Response}",
                (int)response.StatusCode,
                display);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = responseJson.Length > 500 ? responseJson[..500] : responseJson;
            throw new HttpRequestException($"数据湖查询失败（{(int)response.StatusCode}）：{errorBody}");
        }

        try
        {
            return JsonSerializer.Deserialize<List<Dictionary<string, object>>>(responseJson);
        }
        catch (JsonException ex)
        {
            _logger.LogError(
                ex,
                "数据湖响应反序列化失败，ServerCode={ServerCode}，原始响应：{Response}",
                request.ServerCode,
                responseJson.Length > 500 ? responseJson[..500] : responseJson);
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendQueryWithRetryAsync(
        DataLakeConfig config,
        string json,
        CancellationToken ct)
    {
        var response = await SendQueryAsync(config, json, ct);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        _logger.LogWarning("数据湖查询返回 401，尝试刷新 Token 后重试");
        InvalidateToken();
        return await SendQueryAsync(config, json, ct);
    }

    /// <summary>
    /// 发送查询请求。
    /// </summary>
    private async Task<HttpResponseMessage> SendQueryAsync(
        DataLakeConfig config,
        string json,
        CancellationToken ct)
    {
        var token = await GetTokenAsync(ct);

        if (config.DebugLogEnabled)
            _logger.LogInformation("[数据湖调试] 请求参数：{Json}", json);

        var client = CreateClient(config);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await WaitRequestIntervalAsync(config, ct);
            var response = await client.PostAsync(config.QueryEndpoint, content, ct);
            sw.Stop();
            _logger.LogInformation(
                "数据湖请求完成：耗时 {Elapsed}ms，状态码 {StatusCode}",
                sw.ElapsedMilliseconds,
                (int)response.StatusCode);
            return response;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            _logger.LogError("数据湖请求超时：已等待 {Elapsed}ms", sw.ElapsedMilliseconds);
            throw new TimeoutException($"数据湖请求超时（{sw.ElapsedMilliseconds}ms）");
        }
    }

    /// <summary>
    /// 清空 Token 缓存。
    /// </summary>
    private void InvalidateToken()
    {
        _cachedToken = null;
        _tokenExpiry = DateTime.MinValue;
    }

    private async Task WaitRequestIntervalAsync(DataLakeConfig config, CancellationToken ct)
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

    private HttpClient CreateClient(DataLakeConfig config)
    {
        var client = _httpClientFactory.CreateClient("DataLake");
        client.BaseAddress = new Uri(config.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(QueryTimeoutSeconds);
        return client;
    }

    private static string BuildRequestUrl(string baseUrl, string endpoint)
        => new Uri(new Uri(baseUrl), endpoint).ToString();
}
