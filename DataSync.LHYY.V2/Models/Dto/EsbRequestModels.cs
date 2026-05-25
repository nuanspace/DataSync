using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataSync.LHYY.V2.Models.Dto;

/// <summary>
/// ESB 请求外层包装：{ "Request": { "Head": {...}, "Body": ... } }
/// </summary>
public class EsbRequestWrapper
{
    [JsonPropertyName("Request")]
    public EsbRequestInner Request { get; set; } = new();
}

public class EsbRequestInner
{
    [JsonPropertyName("Head")]
    public EsbRequestHead Head { get; set; } = new();

    [JsonPropertyName("Body")]
    public JsonElement? Body { get; set; }
}

public class EsbRequestHead
{
    [JsonPropertyName("Version")]
    public string? Version { get; set; }

    [JsonPropertyName("TranCode")]
    public string? TranCode { get; set; }

    [JsonPropertyName("MessageId")]
    public string? MessageId { get; set; }

    [JsonPropertyName("AppId")]
    public string? AppId { get; set; }

    [JsonPropertyName("OrgId")]
    public string? OrgId { get; set; }

    [JsonPropertyName("ContentEncoding")]
    public string? ContentEncoding { get; set; }

    [JsonPropertyName("Timestamp")]
    public string? Timestamp { get; set; }
}

/// <summary>
/// ESB 响应外层包装
/// </summary>
public class EsbResponseWrapper
{
    [JsonPropertyName("Response")]
    public EsbResponseInner Response { get; set; } = new();
}

public class EsbResponseInner
{
    [JsonPropertyName("Head")]
    public EsbResponseHead Head { get; set; } = new();

    [JsonPropertyName("Body")]
    public string Body { get; set; } = "";
}

public class EsbResponseHead
{
    [JsonPropertyName("AckCode")]
    public string AckCode { get; set; } = "100";

    [JsonPropertyName("AckMessage")]
    public string AckMessage { get; set; } = "成功";

    [JsonPropertyName("TranCode")]
    public string? TranCode { get; set; }

    [JsonPropertyName("MessageId")]
    public string? MessageId { get; set; }
}
