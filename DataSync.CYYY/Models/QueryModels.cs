using System.Text.Json.Serialization;

namespace DataSync.CYYY.Models;

/// <summary>
/// 数据湖查询请求
/// </summary>
public class DataLakeQueryRequest
{
    [JsonPropertyName("sysCode")]
    public string SysCode { get; set; } = "";

    [JsonPropertyName("serverCode")]
    public string ServerCode { get; set; } = "";

    [JsonPropertyName("condition")]
    public List<DataLakeCondition> Condition { get; set; } = [];

    [JsonPropertyName("pageNo")]
    public int PageNo { get; set; } = 1;

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; } = 100;

    [JsonPropertyName("maxResultSize")]
    public int MaxResultSize { get; set; } = 10000;
}

/// <summary>
/// 通用查询条件项，操作符值由所属 API 平台配置。
/// </summary>
public class QueryCondition
{
    [JsonPropertyName("column")]
    public string Column { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("value")]
    public object Value { get; set; } = "";
}

/// <summary>
/// 旧数据湖客户端兼容类型。
/// </summary>
public sealed class DataLakeCondition : QueryCondition
{
}

// 数据湖查询响应：直接返回 JSON 数组，无外层包装

/// <summary>
/// Token 响应外层
/// </summary>
public class TokenApiResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("data")]
    public TokenData? Data { get; set; }
}

/// <summary>
/// Token 响应数据
/// </summary>
public class TokenData
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = "";

    [JsonPropertyName("tokenHead")]
    public string TokenHead { get; set; } = "";

    [JsonPropertyName("expiresIn")]
    public int ExpiresIn { get; set; }
}
