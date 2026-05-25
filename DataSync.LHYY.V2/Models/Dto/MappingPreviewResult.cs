using DataSync.LHYY.V2.Models.Enums;

namespace DataSync.LHYY.V2.Models.Dto;

/// <summary>
/// 映射预览结果
/// </summary>
public class MappingPreviewResult
{
    public int MappingId { get; set; }
    public string SourcePath { get; set; } = "";
    public string TargetField { get; set; } = "";
    public MappingTarget MappingTarget { get; set; }
    /// <summary>JSON 提取原始值</summary>
    public string? RawValue { get; set; }
    /// <summary>字典转换后</summary>
    public string? DictTranslatedValue { get; set; }
    /// <summary>最终值（含表达式处理）</summary>
    public string? FinalValue { get; set; }
    public bool IsRequired { get; set; }
    /// <summary>值为空</summary>
    public bool IsMissing { get; set; }
    public string? Description { get; set; }
}
