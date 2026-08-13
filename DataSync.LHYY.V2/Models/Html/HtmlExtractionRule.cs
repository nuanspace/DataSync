using DataSync.LHYY.V2.Models.Enums;

namespace DataSync.LHYY.V2.Models.Html;

/// <summary>
/// HTML 文本字段提取规则。
/// </summary>
public sealed class HtmlExtractionRule
{
    public string FieldCode { get; set; } = "";
    public string? SourceSection { get; set; }
    public string? SourceLabel { get; set; }
    public HtmlExtractionType ExtractionType { get; set; } = HtmlExtractionType.LabelValue;
    public string? Pattern { get; set; }
    public bool IsRequired { get; set; }
    public int SortOrder { get; set; }
}
