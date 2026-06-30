using DataSync.LHYY.V2.Models.Enums;

namespace DataSync.LHYY.V2.Models.Dto;

/// <summary>
/// 整条消息映射预览结果
/// </summary>
public class MessageMappingPreviewResult
{
    public long MessageId { get; set; }
    public string TranCode { get; set; } = "";
    public string? TranName { get; set; }
    public string? InterfaceName { get; set; }
    public HandlerType HandlerType { get; set; }
    public bool IsFiltered { get; set; }
    public string? FilterReason { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<MessageMappingPreviewSection> Sections { get; set; } = [];

    public int TotalValueCount => Sections.Sum(section => section.Rows.Count);
}

/// <summary>
/// 映射预览分组
/// </summary>
public class MessageMappingPreviewSection
{
    public string Name { get; set; } = "";
    public List<MessageMappingPreviewRow> Rows { get; set; } = [];
}

/// <summary>
/// 映射预览单行
/// </summary>
public class MessageMappingPreviewRow
{
    public string? GroupName { get; set; }
    public string Target { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Value { get; set; }
    public string? Note { get; set; }
    public bool IsWarning { get; set; }
}
