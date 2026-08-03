using System.Text.Json.Serialization;

namespace DataSync.CYYY.Models;

/// <summary>
/// 单个 DynamicApi 接口的增量导入配置；空字段表示保留现有值。
/// </summary>
public sealed class DynamicApiInterfaceImport
{
    [JsonPropertyName("taskCode")]
    public string TaskCode { get; set; } = "";

    [JsonPropertyName("interfaceKey")]
    public string InterfaceKey { get; set; } = "";

    [JsonPropertyName("serverCode")]
    public string? ServerCode { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("queryPath")]
    public string? QueryPath { get; set; }

    [JsonPropertyName("useTodayTimeRange")]
    public bool? UseTodayTimeRange { get; set; }

    [JsonPropertyName("accessWindowEnabled")]
    public bool? AccessWindowEnabled { get; set; }

    [JsonPropertyName("accessWindowStart")]
    public string? AccessWindowStart { get; set; }

    [JsonPropertyName("accessWindowEnd")]
    public string? AccessWindowEnd { get; set; }

    [JsonPropertyName("isRequired")]
    public bool? IsRequired { get; set; }

    [JsonPropertyName("sortOrder")]
    public int? SortOrder { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("pushParams")]
    public Dictionary<string, string>? PushParams { get; set; }

    [JsonPropertyName("injectFields")]
    public List<string>? InjectFields { get; set; }

    [JsonPropertyName("outputFields")]
    public string? OutputFields { get; set; }
}
