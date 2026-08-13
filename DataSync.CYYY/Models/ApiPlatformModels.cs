using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataSync.CYYY.Models;

[Table("api_platform_configs", Schema = "cyyy")]
public class ApiPlatformConfig
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("base_url")]
    public string BaseUrl { get; set; } = "";

    [Column("auth_config", TypeName = "jsonb")]
    public string AuthConfig { get; set; } = "{}";

    [Column("query_config", TypeName = "jsonb")]
    public string QueryConfig { get; set; } = "{}";

    [Column("response_config", TypeName = "jsonb")]
    public string ResponseConfig { get; set; } = "{}";

    [Column("request_interval_milliseconds")]
    public int RequestIntervalMilliseconds { get; set; } = 200;

    [Column("ignore_ssl_errors")]
    public bool IgnoreSslErrors { get; set; }

    [Column("debug_log_enabled")]
    public bool DebugLogEnabled { get; set; }

    [Column("enabled")]
    public bool Enabled { get; set; } = true;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ApiAuthConfig GetAuthConfig() => ApiPlatformJson.Deserialize<ApiAuthConfig>(AuthConfig);
    public ApiQueryConfig GetQueryConfig() => ApiPlatformJson.Deserialize<ApiQueryConfig>(QueryConfig);
    public ApiResponseConfig GetResponseConfig() => ApiPlatformJson.Deserialize<ApiResponseConfig>(ResponseConfig);
}

[Table("api_interfaces", Schema = "cyyy")]
public class ApiInterface
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("api_platform_id")]
    public int ApiPlatformId { get; set; }

    [Column("code")]
    public string Code { get; set; } = "";

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("relative_path")]
    public string RelativePath { get; set; } = "";

    [ForeignKey(nameof(ApiPlatformId))]
    public ApiPlatformConfig? Platform { get; set; }
}

public sealed class ApiAuthConfig
{
    public string TokenEndpoint { get; set; } = "";
    public string RequestType { get; set; } = ApiRequestTypes.Json;
    public List<ApiParameterConfig> Parameters { get; set; } = [];
    public string BusinessCodePath { get; set; } = "";
    public string ExpectedBusinessCode { get; set; } = "";
    public string MessagePath { get; set; } = "";
    public string TokenPath { get; set; } = "";
    public string ExpiryPath { get; set; } = "";
    public string ExpiryMode { get; set; } = ApiTokenExpiryModes.RelativeSeconds;
    public int RefreshAdvanceSeconds { get; set; } = 60;
    public string HeaderName { get; set; } = "Authorization";
    public string HeaderScheme { get; set; } = "Bearer";
}

public sealed class ApiQueryConfig
{
    public string EndpointTemplate { get; set; } = "";
    public string ParameterMode { get; set; } = ApiParameterModes.DirectProperties;
    public List<ApiParameterConfig> FixedParameters { get; set; } = [];
    public string InterfaceCodeField { get; set; } = "";
    public ApiConditionArrayConfig? ConditionArray { get; set; }
    public bool PaginationEnabled { get; set; } = true;
    public string PageNumberField { get; set; } = "pageNum";
    public string PageSizeField { get; set; } = "pageSize";
    public int PageSize { get; set; } = 100;
    public string MaxResultSizeField { get; set; } = "";
    public int? MaxResultSize { get; set; }
    public string StartTimeField { get; set; } = "startTime";
    public string EndTimeField { get; set; } = "endTime";
    public string DateTimeFormat { get; set; } = "yyyy-MM-dd HH:mm:ss";
    public bool TimeRangeUsesPagination { get; set; } = true;
}

public sealed class ApiConditionArrayConfig
{
    public string ArrayField { get; set; } = "condition";
    public string ColumnField { get; set; } = "column";
    public string OperatorField { get; set; } = "type";
    public string ValueField { get; set; } = "value";
    public List<ApiOperatorConfig> Operators { get; set; } = [];
    public string SingleValueOperator { get; set; } = "";
    public string MultiValueOperator { get; set; } = "";
    public string StartTimeOperator { get; set; } = "";
    public string EndTimeOperator { get; set; } = "";
}

public sealed class ApiOperatorConfig
{
    public string Value { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool RequiresValue { get; set; } = true;
}

public sealed class ApiResponseConfig
{
    public string BusinessCodePath { get; set; } = "";
    public string ExpectedBusinessCode { get; set; } = "";
    public string MessagePath { get; set; } = "";
    public string DataPath { get; set; } = "";
    public string HasMorePath { get; set; } = "";
    public string TotalPagesPath { get; set; } = "";
}

public sealed class ApiParameterConfig
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string ValueType { get; set; } = ApiParameterValueTypes.String;
    public bool IsSecret { get; set; }
}

public sealed class ApiFilterCondition
{
    public string Field { get; set; } = "";
    public string Operator { get; set; } = "";
    public object Value { get; set; } = "";
}

public sealed class InterfaceQueryMapping
{
    public string TargetField { get; set; } = "";
    public string SourceField { get; set; } = "";
}

public static class ApiRequestTypes
{
    public const string Form = "Form";
    public const string Json = "Json";
}

public static class ApiTokenExpiryModes
{
    public const string RelativeSeconds = "RelativeSeconds";
    public const string UnixSeconds = "UnixSeconds";
}

public static class ApiParameterModes
{
    public const string DirectProperties = "DirectProperties";
    public const string ConditionArray = "ConditionArray";
}

public static class ApiParameterValueTypes
{
    public const string String = "String";
    public const string Number = "Number";
    public const string Boolean = "Boolean";
}

public static class ApiPlatformJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static T Deserialize<T>(string? json) where T : new()
    {
        if (string.IsNullOrWhiteSpace(json))
            return new T();

        return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
