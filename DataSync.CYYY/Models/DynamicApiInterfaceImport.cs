using System.Text.Json.Serialization;

namespace DataSync.CYYY.Models;

/// <summary>
/// 单个 API 接口的增量导入配置；空字段表示保留现有值。
/// </summary>
public sealed class ApiTaskInterfacePatchImport
{
    [JsonPropertyName("taskCode")]
    public string TaskCode { get; set; } = "";

    [JsonPropertyName("interfaceKey")]
    public string InterfaceKey { get; set; } = "";

    [JsonPropertyName("platformName")]
    public string PlatformName { get; set; } = "";

    [JsonPropertyName("serverCode")]
    public string? ServerCode { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("queryPath")]
    public string? QueryPath { get; set; }

    [JsonPropertyName("useTodayTimeRange")]
    public bool? UseTodayTimeRange { get; set; }

    [JsonPropertyName("continuousPollingEnabled")]
    public bool? ContinuousPollingEnabled { get; set; }

    [JsonPropertyName("continuousPollingIntervalSeconds")]
    public int? ContinuousPollingIntervalSeconds { get; set; }

    [JsonPropertyName("patientContinuousSyncEnabled")]
    public bool? PatientContinuousSyncEnabled { get; set; }

    [JsonPropertyName("continuousUseTimeRange")]
    public bool? ContinuousUseTimeRange { get; set; }

    [JsonPropertyName("continuousRecordKeyFields")]
    public List<string>? ContinuousRecordKeyFields { get; set; }

    [JsonPropertyName("continuousUseRowHash")]
    public bool? ContinuousUseRowHash { get; set; }

    [JsonPropertyName("queryStartTimeSourceServerCode")]
    public string? QueryStartTimeSourceServerCode { get; set; }

    [JsonPropertyName("queryStartTimeSourceField")]
    public string? QueryStartTimeSourceField { get; set; }

    [JsonPropertyName("queryEndTimeSourceServerCode")]
    public string? QueryEndTimeSourceServerCode { get; set; }

    [JsonPropertyName("queryEndTimeSourceField")]
    public string? QueryEndTimeSourceField { get; set; }

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

    [JsonPropertyName("queryMappings")]
    public List<InterfaceQueryMapping>? QueryMappings { get; set; }
}
