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

/// <summary>
/// 手工样本值试算结果
/// </summary>
public class MappingSamplePreviewResult
{
    public bool PassedFilter { get; set; } = true;
    public string? RawValue { get; set; }
    public string? DictTranslatedValue { get; set; }
    public string? FinalValue { get; set; }
    public bool IsDictMatched { get; set; }
    public bool IsMissing { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<MappingPreviewStep> Steps { get; set; } = [];
}

/// <summary>
/// 映射试算步骤
/// </summary>
public class MappingPreviewStep
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string? InputValue { get; set; }
    public string? OutputValue { get; set; }
    public string? Message { get; set; }
}
