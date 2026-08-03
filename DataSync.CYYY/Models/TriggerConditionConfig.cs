using System.Text.Json.Serialization;

namespace DataSync.CYYY.Models;

/// <summary>
/// 同步触发条件配置（对应 SyncTask.TriggerConditions 的新 JSON 格式）
/// </summary>
public class TriggerConditionConfig
{
    /// <summary>
    /// 条件逻辑：AND / OR
    /// </summary>
    [JsonPropertyName("logic")]
    public string Logic { get; set; } = "AND";

    /// <summary>
    /// 是否排除已同步记录
    /// </summary>
    [JsonPropertyName("excludeSynced")]
    public bool ExcludeSynced { get; set; } = true;

    /// <summary>
    /// 过滤规则列表
    /// </summary>
    [JsonPropertyName("rules")]
    public List<ConditionRule> Rules { get; set; } = [];
}

/// <summary>
/// 单条过滤规则
/// </summary>
public class ConditionRule
{
    /// <summary>
    /// 接口 ServerCode（如 JHIDS-BAS-OAP-028），为空时回退到任务的 TriggerServerCode
    /// </summary>
    [JsonPropertyName("interface")]
    public string Interface { get; set; } = "";

    /// <summary>
    /// 字段名（如 SC_APPLY_DEPT）
    /// </summary>
    [JsonPropertyName("field")]
    public string Field { get; set; } = "";

    /// <summary>
    /// 模式：include（接受）/ exclude（排除）/ contains（包含）/ not_null（非空）
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "include";

    /// <summary>
    /// 值列表（not_null 模式下为空）
    /// </summary>
    [JsonPropertyName("values")]
    public List<string> Values { get; set; } = [];
}
