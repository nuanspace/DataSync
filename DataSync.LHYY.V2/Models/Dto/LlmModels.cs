using DataSync.LHYY.V2.Models.Enums;

namespace DataSync.LHYY.V2.Models.Dto;

/// <summary>
/// LLM 映射建议
/// </summary>
public class MappingSuggestion
{
    public string SourcePath { get; set; } = "";
    public string TargetField { get; set; } = "";
    public MappingTarget MappingTarget { get; set; }
    /// <summary>置信度 0-1</summary>
    public double Confidence { get; set; }
    /// <summary>推荐理由</summary>
    public string? Reason { get; set; }
    /// <summary>是否建议字典转换</summary>
    public string? DictCode { get; set; }
    /// <summary>LLM 建议的默认值</summary>
    public string? DefaultValue { get; set; }
    /// <summary>SubCard 卡片 ID</summary>
    public Guid? CardId { get; set; }
    /// <summary>SubCard 数组路径</summary>
    public string? ArrayPath { get; set; }
    /// <summary>同一目标字段中置信度最高的标记为 true</summary>
    public bool IsBest { get; set; }
}

/// <summary>
/// 目标字段信息（供 LLM 参考）
/// </summary>
public class TargetFieldInfo
{
    public string FieldId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string DataType { get; set; } = "";
    public MappingTarget Category { get; set; }
    /// <summary>字段补充语义（如标签、提示语、单位说明）</summary>
    public string? SemanticHint { get; set; }
    /// <summary>所属卡片 ID（SubCard 问题用）</summary>
    public Guid? CardId { get; set; }
    /// <summary>所属卡片名称（供 LLM 语义匹配）</summary>
    public string? CardName { get; set; }
}

/// <summary>
/// LLM 接口配置分析结果
/// </summary>
public class InterfaceConfigSuggestion
{
    public string TranCode { get; set; } = "";
    public string TranName { get; set; } = "";
    public string EventTypeName { get; set; } = "";
    public string MrnSourcePath { get; set; } = "";
    public string EventStartTimeSourcePath { get; set; } = "";
    public string MainRecordArrayPath { get; set; } = "";
    public string Description { get; set; } = "";
}
