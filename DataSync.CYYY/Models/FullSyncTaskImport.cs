using System.Text.Json.Serialization;

namespace DataSync.CYYY.Models;

/// <summary>
/// 一个 DynamicApi 采集源、一个同步任务及其全部关联接口的导入配置。
/// </summary>
public sealed class FullSyncTaskImport
{
    [JsonPropertyName("source")]
    public DynamicApiIngestionImport? Source { get; set; }

    [JsonPropertyName("task")]
    public SyncTaskImport? Task { get; set; }

    [JsonPropertyName("interfaces")]
    public List<DynamicApiTaskInterfaceImport>? Interfaces { get; set; }
}

public sealed class DynamicApiIngestionImport
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("serverCode")]
    public string ServerCode { get; set; } = "";

    [JsonPropertyName("queryPath")]
    public string QueryPath { get; set; } = "";

    [JsonPropertyName("timeField")]
    public string TimeField { get; set; } = "";

    [JsonPropertyName("primaryKeys")]
    public List<string> PrimaryKeys { get; set; } = [];

    [JsonPropertyName("lookbackMinutes")]
    public int LookbackMinutes { get; set; } = 5;

    [JsonPropertyName("pollingIntervalSeconds")]
    public int PollingIntervalSeconds { get; set; } = 300;

    [JsonPropertyName("startOffsetMinutes")]
    public int StartOffsetMinutes { get; set; } = 120;

    [JsonPropertyName("endOffsetMinutes")]
    public int EndOffsetMinutes { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public sealed class SyncTaskImport
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("pushType")]
    public string PushType { get; set; } = "Api";

    [JsonPropertyName("pushTarget")]
    public string PushTarget { get; set; } = "";

    [JsonPropertyName("patientIdField")]
    public string PatientIdField { get; set; } = "";

    [JsonPropertyName("visitSnField")]
    public string VisitSnField { get; set; } = "";

    [JsonPropertyName("pollingIntervalSeconds")]
    public int PollingIntervalSeconds { get; set; } = 300;

    [JsonPropertyName("patientConcurrency")]
    public int PatientConcurrency { get; set; } = 5;

    [JsonPropertyName("apiConcurrency")]
    public int ApiConcurrency { get; set; } = 3;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("enableTriggerRecordPush")]
    public bool EnableTriggerRecordPush { get; set; }

    [JsonPropertyName("triggerPushTarget")]
    public string? TriggerPushTarget { get; set; }

    [JsonPropertyName("triggerPushParams")]
    public Dictionary<string, string>? TriggerPushParams { get; set; }
}

public sealed class DynamicApiTaskInterfaceImport
{
    [JsonPropertyName("interfaceKey")]
    public string InterfaceKey { get; set; } = "";

    [JsonPropertyName("serverCode")]
    public string ServerCode { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("queryPath")]
    public string QueryPath { get; set; } = "";

    [JsonPropertyName("useTodayTimeRange")]
    public bool UseTodayTimeRange { get; set; }

    [JsonPropertyName("accessWindowEnabled")]
    public bool AccessWindowEnabled { get; set; }

    [JsonPropertyName("accessWindowStart")]
    public string? AccessWindowStart { get; set; }

    [JsonPropertyName("accessWindowEnd")]
    public string? AccessWindowEnd { get; set; }

    [JsonPropertyName("isRequired")]
    public bool IsRequired { get; set; }

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; } = 1;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("pushParams")]
    public Dictionary<string, string>? PushParams { get; set; }

    [JsonPropertyName("injectFields")]
    public List<string>? InjectFields { get; set; }

    [JsonPropertyName("outputFields")]
    public string? OutputFields { get; set; }
}
