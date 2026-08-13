using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DataSync.CYYY.Data;
using DataSync.CYYY.Models;
using Microsoft.EntityFrameworkCore;

namespace DataSync.CYYY.Services;

public sealed class ApiPlatformClient
{
    private const int QueryTimeoutSeconds = 120;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDbContextFactory<SyncDbContext> _dbFactory;
    private readonly ILogger<ApiPlatformClient> _logger;
    private readonly ConcurrentDictionary<int, PlatformRuntimeState> _states = new();

    public ApiPlatformClient(
        IHttpClientFactory httpClientFactory,
        IDbContextFactory<SyncDbContext> dbFactory,
        ILogger<ApiPlatformClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public void ReloadConfig(int? platformId = null)
    {
        if (platformId.HasValue)
            _states.TryRemove(platformId.Value, out _);
        else
            _states.Clear();
    }

    public async Task<bool> HasConfigAsync(int apiInterfaceId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ApiInterfaces.AnyAsync(i =>
            i.Id == apiInterfaceId && i.Platform != null && i.Platform.Enabled, ct);
    }

    public async Task TestConnectionAsync(ApiPlatformConfig platform, CancellationToken ct)
    {
        ValidatePlatform(platform);
        await RequestTokenAsync(platform, platform.GetAuthConfig(), ct);
    }

    public async Task<(int TotalCount, int PageCount)> QueryPagesAsync(
        ApiInterface apiInterface,
        IReadOnlyDictionary<string, object?>? mappedValues,
        IReadOnlyCollection<ApiFilterCondition>? filters,
        DateTime? from,
        DateTime? to,
        string? timeField,
        Func<List<Dictionary<string, object>>, int, Task> onPageAsync,
        CancellationToken ct,
        bool? usePagination = null)
    {
        apiInterface = await LoadInterfaceAsync(apiInterface, ct);
        var platform = apiInterface.Platform!;
        ValidatePlatform(platform);
        if (!platform.Enabled)
            throw new InvalidOperationException($"平台 [{platform.Name}] 已停用");
        var queryConfig = platform.GetQueryConfig();
        var responseConfig = platform.GetResponseConfig();
        ValidateQueryConfig(queryConfig);

        var paginate = usePagination ?? queryConfig.PaginationEnabled;
        var pageNumber = 1;
        var pageCount = 0;
        var totalCount = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var body = BuildQueryBody(
                apiInterface,
                queryConfig,
                mappedValues,
                filters,
                from,
                to,
                timeField,
                paginate ? pageNumber : null);

            using var document = await ExecuteQueryAsync(platform, queryConfig, responseConfig, apiInterface, body, ct);
            var records = ReadData(document.RootElement, responseConfig.DataPath);
            if (records.Count > 0)
            {
                pageCount++;
                totalCount += records.Count;
                await onPageAsync(records, pageNumber);
            }

            if (!paginate || !HasNextPage(document.RootElement, responseConfig, pageNumber, records.Count, queryConfig.PageSize))
                break;

            pageNumber++;
        }

        return (totalCount, pageCount);
    }

    public async Task<List<Dictionary<string, object>>> QueryAllPagesAsync(
        ApiInterface apiInterface,
        IReadOnlyDictionary<string, object?>? mappedValues,
        IReadOnlyCollection<ApiFilterCondition>? filters,
        DateTime? from,
        DateTime? to,
        string? timeField,
        CancellationToken ct,
        bool? usePagination = null)
    {
        var result = new List<Dictionary<string, object>>();
        await QueryPagesAsync(
            apiInterface,
            mappedValues,
            filters,
            from,
            to,
            timeField,
            (records, _) =>
            {
                result.AddRange(records);
                return Task.CompletedTask;
            },
            ct,
            usePagination);
        return result;
    }

    private async Task<ApiInterface> LoadInterfaceAsync(ApiInterface apiInterface, CancellationToken ct)
    {
        if (apiInterface.Platform != null)
            return apiInterface;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ApiInterfaces
            .AsNoTracking()
            .Include(i => i.Platform)
            .FirstOrDefaultAsync(i => i.Id == apiInterface.Id, ct)
            ?? throw new InvalidOperationException("API 接口目录不存在");
    }

    private async Task<JsonDocument> ExecuteQueryAsync(
        ApiPlatformConfig platform,
        ApiQueryConfig queryConfig,
        ApiResponseConfig responseConfig,
        ApiInterface apiInterface,
        Dictionary<string, object?> body,
        CancellationToken ct)
    {
        var endpoint = queryConfig.EndpointTemplate.Replace(
            "{path}",
            apiInterface.RelativePath.Trim('/'),
            StringComparison.OrdinalIgnoreCase);
        using var response = await SendQueryWithRetryAsync(platform, endpoint, body, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (platform.DebugLogEnabled)
        {
            var display = json.Length > 2000 ? $"{json[..2000]}...（共 {json.Length} 字符）" : json;
            _logger.LogInformation("[{Platform}] 返回数据（{StatusCode}）：{Response}", platform.Name, (int)response.StatusCode, display);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = json.Length > 500 ? json[..500] : json;
            throw new HttpRequestException($"[{platform.Name}] 查询失败（{(int)response.StatusCode}）：{errorBody}");
        }

        var document = JsonDocument.Parse(json);
        try
        {
            ValidateBusinessResponse(document.RootElement, responseConfig.BusinessCodePath,
                responseConfig.ExpectedBusinessCode, responseConfig.MessagePath, platform.Name);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendQueryWithRetryAsync(
        ApiPlatformConfig platform,
        string endpoint,
        Dictionary<string, object?> body,
        CancellationToken ct)
    {
        var response = await SendQueryAsync(platform, endpoint, body, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        InvalidateToken(platform.Id);
        return await SendQueryAsync(platform, endpoint, body, ct);
    }

    private async Task<HttpResponseMessage> SendQueryAsync(
        ApiPlatformConfig platform,
        string endpoint,
        Dictionary<string, object?> body,
        CancellationToken ct)
    {
        var authConfig = platform.GetAuthConfig();
        var token = await GetTokenAsync(platform, authConfig, ct);
        var client = CreateClient(platform);
        SetAuthorizationHeader(client, authConfig, token);
        await WaitRequestIntervalAsync(platform, ct);

        if (platform.DebugLogEnabled)
            _logger.LogInformation("[{Platform}] 查询请求：{Body}", platform.Name, JsonSerializer.Serialize(body));

        return await client.PostAsJsonAsync(endpoint, body, ct);
    }

    private async Task<string> GetTokenAsync(ApiPlatformConfig platform, ApiAuthConfig authConfig, CancellationToken ct)
    {
        var state = _states.GetOrAdd(platform.Id, _ => new PlatformRuntimeState());
        if (!string.IsNullOrWhiteSpace(state.Token) && DateTimeOffset.UtcNow < state.TokenExpiry)
            return state.Token;

        await state.TokenLock.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(state.Token) && DateTimeOffset.UtcNow < state.TokenExpiry)
                return state.Token;

            var tokenResult = await RequestTokenAsync(platform, authConfig, ct);
            state.Token = tokenResult.Token;
            state.TokenExpiry = tokenResult.Expiry;
            return state.Token;
        }
        finally
        {
            state.TokenLock.Release();
        }
    }

    private async Task<(string Token, DateTimeOffset Expiry)> RequestTokenAsync(
        ApiPlatformConfig platform,
        ApiAuthConfig authConfig,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(authConfig.TokenEndpoint))
            throw new InvalidOperationException($"平台 [{platform.Name}] 未配置 Token 地址");

        var client = CreateClient(platform);
        await WaitRequestIntervalAsync(platform, ct);
        using HttpResponseMessage response = string.Equals(authConfig.RequestType, ApiRequestTypes.Form, StringComparison.OrdinalIgnoreCase)
            ? await client.PostAsync(authConfig.TokenEndpoint, new FormUrlEncodedContent(
                authConfig.Parameters.ToDictionary(p => p.Name, p => p.Value)), ct)
            : await client.PostAsJsonAsync(authConfig.TokenEndpoint,
                authConfig.Parameters.ToDictionary(p => p.Name, p => ConvertConfiguredValue(p)), ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"平台 [{platform.Name}] Token 请求失败（{(int)response.StatusCode}）");

        using var document = JsonDocument.Parse(json);
        ValidateBusinessResponse(document.RootElement, authConfig.BusinessCodePath,
            authConfig.ExpectedBusinessCode, authConfig.MessagePath, platform.Name);

        var token = ReadPath(document.RootElement, authConfig.TokenPath)?.ToString();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException($"平台 [{platform.Name}] Token 路径未读取到值");

        var expiry = ResolveTokenExpiry(document.RootElement, authConfig);
        return (token, expiry);
    }

    private async Task WaitRequestIntervalAsync(ApiPlatformConfig platform, CancellationToken ct)
    {
        var intervalMs = Math.Max(0, platform.RequestIntervalMilliseconds);
        if (intervalMs == 0)
            return;

        var state = _states.GetOrAdd(platform.Id, _ => new PlatformRuntimeState());
        await state.RequestLock.WaitAsync(ct);
        try
        {
            var wait = TimeSpan.FromMilliseconds(intervalMs) - (DateTime.UtcNow - state.LastRequestAt);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            state.LastRequestAt = DateTime.UtcNow;
        }
        finally
        {
            state.RequestLock.Release();
        }
    }

    private HttpClient CreateClient(ApiPlatformConfig platform)
    {
        var client = _httpClientFactory.CreateClient(platform.IgnoreSslErrors ? "ApiPlatformInsecure" : "ApiPlatform");
        client.BaseAddress = new Uri(platform.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(QueryTimeoutSeconds);
        return client;
    }

    private static Dictionary<string, object?> BuildQueryBody(
        ApiInterface apiInterface,
        ApiQueryConfig queryConfig,
        IReadOnlyDictionary<string, object?>? mappedValues,
        IReadOnlyCollection<ApiFilterCondition>? filters,
        DateTime? from,
        DateTime? to,
        string? timeField,
        int? pageNumber)
    {
        var body = queryConfig.FixedParameters.ToDictionary(
            parameter => parameter.Name,
            parameter => (object?)ConvertConfiguredValue(parameter));
        if (!string.IsNullOrWhiteSpace(queryConfig.InterfaceCodeField))
            body[queryConfig.InterfaceCodeField] = apiInterface.Code;

        if (string.Equals(queryConfig.ParameterMode, ApiParameterModes.ConditionArray, StringComparison.OrdinalIgnoreCase))
        {
            var config = queryConfig.ConditionArray
                ?? throw new InvalidOperationException("条件数组参数模式缺少条件结构配置");
            var conditions = new List<Dictionary<string, object?>>();
            if (mappedValues != null)
            {
                conditions.AddRange(mappedValues.Select(item => BuildMappedCondition(config, item.Key, item.Value)));
            }

            if (filters != null)
            {
                foreach (var filter in filters)
                {
                    ValidateOperator(config, filter.Operator);
                    conditions.Add(BuildCondition(config, filter.Field, filter.Operator, filter.Value));
                }
            }

            if (from.HasValue || to.HasValue)
            {
                if (string.IsNullOrWhiteSpace(timeField))
                    throw new InvalidOperationException("条件数组时间查询必须配置时间字段");
                if (from.HasValue)
                    conditions.Add(BuildCondition(config, timeField, config.StartTimeOperator,
                        from.Value.ToString(queryConfig.DateTimeFormat)));
                if (to.HasValue)
                    conditions.Add(BuildCondition(config, timeField, config.EndTimeOperator,
                        to.Value.ToString(queryConfig.DateTimeFormat)));
            }

            body[config.ArrayField] = conditions;
        }
        else
        {
            if (mappedValues != null)
            {
                foreach (var item in mappedValues)
                    body[item.Key] = item.Value;
            }

            if (filters is { Count: > 0 })
                throw new InvalidOperationException("当前平台使用直接参数，不支持条件数组过滤");

            if (from.HasValue && !string.IsNullOrWhiteSpace(queryConfig.StartTimeField))
                body[queryConfig.StartTimeField] = from.Value.ToString(queryConfig.DateTimeFormat);
            if (to.HasValue && !string.IsNullOrWhiteSpace(queryConfig.EndTimeField))
                body[queryConfig.EndTimeField] = to.Value.ToString(queryConfig.DateTimeFormat);
        }

        if (pageNumber.HasValue)
        {
            if (!string.IsNullOrWhiteSpace(queryConfig.PageNumberField))
                body[queryConfig.PageNumberField] = pageNumber.Value;
            if (!string.IsNullOrWhiteSpace(queryConfig.PageSizeField))
                body[queryConfig.PageSizeField] = queryConfig.PageSize;
            if (!string.IsNullOrWhiteSpace(queryConfig.MaxResultSizeField) && queryConfig.MaxResultSize.HasValue)
                body[queryConfig.MaxResultSizeField] = queryConfig.MaxResultSize.Value;
        }

        return body;
    }

    private static Dictionary<string, object?> BuildMappedCondition(
        ApiConditionArrayConfig config,
        string field,
        object? value)
    {
        var values = ToValueList(value);
        var operation = values.Count > 1 ? config.MultiValueOperator : config.SingleValueOperator;
        var conditionValue = values.Count > 1 ? string.Join(',', values) : values.FirstOrDefault() ?? "";
        return BuildCondition(config, field, operation, conditionValue);
    }

    private static Dictionary<string, object?> BuildCondition(
        ApiConditionArrayConfig config,
        string field,
        string operation,
        object? value)
    {
        ValidateOperator(config, operation);
        var result = new Dictionary<string, object?>
        {
            [config.ColumnField] = field,
            [config.OperatorField] = operation
        };
        var operatorConfig = config.Operators.First(item =>
            string.Equals(item.Value, operation, StringComparison.OrdinalIgnoreCase));
        result[config.ValueField] = operatorConfig.RequiresValue ? value : "";
        return result;
    }

    private static void ValidateOperator(ApiConditionArrayConfig config, string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !config.Operators.Any(item =>
                string.Equals(item.Value, operation, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"操作符 [{operation}] 未在当前平台配置中定义");
        }
    }

    private static List<string> ToValueList(object? value)
    {
        if (value is IEnumerable<string> stringValues)
            return stringValues.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (value is JsonElement { ValueKind: JsonValueKind.Array } element)
            return element.EnumerateArray().Select(item => item.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        var text = value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? [] : [text];
    }

    private static bool HasNextPage(
        JsonElement root,
        ApiResponseConfig config,
        int pageNumber,
        int recordCount,
        int pageSize)
    {
        if (!string.IsNullOrWhiteSpace(config.HasMorePath) &&
            ReadPath(root, config.HasMorePath) is { } hasMore &&
            (hasMore.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            return hasMore.GetBoolean();
        }

        if (!string.IsNullOrWhiteSpace(config.TotalPagesPath) &&
            ReadPath(root, config.TotalPagesPath) is { } totalPages &&
            totalPages.TryGetInt32(out var total))
        {
            return pageNumber < total;
        }

        return recordCount >= pageSize;
    }

    private static List<Dictionary<string, object>> ReadData(JsonElement root, string dataPath)
    {
        var element = string.IsNullOrWhiteSpace(dataPath) ? root : ReadPath(root, dataPath);
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];
        if (element.Value.ValueKind == JsonValueKind.Object)
        {
            var item = JsonSerializer.Deserialize<Dictionary<string, object>>(element.Value.GetRawText());
            return item == null ? [] : [item];
        }
        if (element.Value.ValueKind == JsonValueKind.Array)
            return JsonSerializer.Deserialize<List<Dictionary<string, object>>>(element.Value.GetRawText()) ?? [];
        throw new InvalidOperationException("平台响应数据不是对象或数组");
    }

    private static JsonElement? ReadPath(JsonElement root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return root;
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind != JsonValueKind.Object)
                return null;
            var found = current.EnumerateObject().FirstOrDefault(property =>
                string.Equals(property.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (found.Equals(default(JsonProperty)))
                return null;
            current = found.Value;
        }
        return current;
    }

    private static void ValidateBusinessResponse(
        JsonElement root,
        string codePath,
        string expectedCode,
        string messagePath,
        string platformName)
    {
        if (string.IsNullOrWhiteSpace(codePath))
            return;
        var code = ReadPath(root, codePath)?.ToString();
        if (string.Equals(code, expectedCode, StringComparison.OrdinalIgnoreCase))
            return;
        var message = ReadPath(root, messagePath)?.ToString();
        throw new InvalidOperationException($"平台 [{platformName}] 请求失败：{message ?? code ?? "未知错误"}");
    }

    private static DateTimeOffset ResolveTokenExpiry(JsonElement root, ApiAuthConfig config)
    {
        var expiryText = ReadPath(root, config.ExpiryPath)?.ToString();
        if (!long.TryParse(expiryText, CultureInfo.InvariantCulture, out var value))
            return DateTimeOffset.UtcNow.AddMinutes(30);
        var expiry = string.Equals(config.ExpiryMode, ApiTokenExpiryModes.UnixSeconds, StringComparison.OrdinalIgnoreCase)
            ? DateTimeOffset.FromUnixTimeSeconds(value)
            : DateTimeOffset.UtcNow.AddSeconds(value);
        return expiry.AddSeconds(-Math.Max(0, config.RefreshAdvanceSeconds));
    }

    private static object ConvertConfiguredValue(ApiParameterConfig parameter)
    {
        if (string.Equals(parameter.ValueType, ApiParameterValueTypes.Number, StringComparison.OrdinalIgnoreCase) &&
            decimal.TryParse(parameter.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            return number;
        if (string.Equals(parameter.ValueType, ApiParameterValueTypes.Boolean, StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(parameter.Value, out var boolean))
            return boolean;
        return parameter.Value;
    }

    private static void SetAuthorizationHeader(HttpClient client, ApiAuthConfig config, string token)
    {
        if (string.Equals(config.HeaderName, "Authorization", StringComparison.OrdinalIgnoreCase))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(config.HeaderScheme, token);
        else
            client.DefaultRequestHeaders.TryAddWithoutValidation(config.HeaderName, $"{config.HeaderScheme} {token}".Trim());
    }

    private void InvalidateToken(int platformId)
    {
        if (_states.TryGetValue(platformId, out var state))
        {
            state.Token = null;
            state.TokenExpiry = DateTimeOffset.MinValue;
        }
    }

    private static void ValidatePlatform(ApiPlatformConfig platform)
    {
        if (string.IsNullOrWhiteSpace(platform.Name) || string.IsNullOrWhiteSpace(platform.BaseUrl))
            throw new InvalidOperationException("平台名称和 BaseUrl 不能为空");
        if (!Uri.TryCreate(platform.BaseUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("BaseUrl 格式无效");
    }

    private static void ValidateQueryConfig(ApiQueryConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.EndpointTemplate))
            throw new InvalidOperationException("平台未配置查询地址模板");
        if (config.PageSize < 1)
            throw new InvalidOperationException("平台分页大小必须大于 0");
        if (!string.Equals(config.ParameterMode, ApiParameterModes.ConditionArray, StringComparison.OrdinalIgnoreCase))
            return;

        var condition = config.ConditionArray
            ?? throw new InvalidOperationException("平台未配置条件数组结构");
        if (string.IsNullOrWhiteSpace(condition.ArrayField) || string.IsNullOrWhiteSpace(condition.ColumnField) ||
            string.IsNullOrWhiteSpace(condition.OperatorField) || string.IsNullOrWhiteSpace(condition.ValueField))
            throw new InvalidOperationException("平台条件数组字段配置不完整");
        if (condition.Operators.Count == 0 || condition.Operators.Any(item => string.IsNullOrWhiteSpace(item.Value)) ||
            condition.Operators.GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("平台操作符配置不完整或存在重复值");
        var roles = new[]
        {
            condition.SingleValueOperator,
            condition.MultiValueOperator,
            condition.StartTimeOperator,
            condition.EndTimeOperator
        };
        if (roles.Any(role => string.IsNullOrWhiteSpace(role) || !condition.Operators.Any(item =>
                string.Equals(item.Value, role, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("平台单值、多值或时间操作符未在操作符列表中定义");
    }

    private sealed class PlatformRuntimeState
    {
        public string? Token { get; set; }
        public DateTimeOffset TokenExpiry { get; set; } = DateTimeOffset.MinValue;
        public DateTime LastRequestAt { get; set; } = DateTime.MinValue;
        public SemaphoreSlim TokenLock { get; } = new(1, 1);
        public SemaphoreSlim RequestLock { get; } = new(1, 1);
    }
}
