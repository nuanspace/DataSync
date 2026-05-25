namespace DataSync.LHYY.V2.Models.Options;

/// <summary>
/// LLM 配置选项
/// </summary>
public class LlmOptions
{
    public const string OnlineProvider = "online";
    public const string LocalProvider = "local";
    public const string ActiveProviderKey = "LlmOptions:ActiveProvider";
    public const string BaseUrlKey = "LlmOptions:BaseUrl";
    public const string ModelKey = "LlmOptions:Model";
    public const string ApiKeyKey = "LlmOptions:ApiKey";
    public const string TimeoutSecondsKey = "LlmOptions:TimeoutSeconds";
    public const string LocalBaseUrlKey = "LlmOptions:Local:BaseUrl";
    public const string LocalModelKey = "LlmOptions:Local:Model";
    public const string LocalTimeoutSecondsKey = "LlmOptions:Local:TimeoutSeconds";

    public string BaseUrl { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "qwen2.5:14b";
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 120;

    public static LlmOptions CreateLocalDefaults() => new()
    {
        TimeoutSeconds = 600
    };
}
