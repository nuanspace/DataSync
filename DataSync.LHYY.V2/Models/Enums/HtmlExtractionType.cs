namespace DataSync.LHYY.V2.Models.Enums;

/// <summary>
/// HTML 文本字段的确定性提取方式。
/// </summary>
public enum HtmlExtractionType : short
{
    LabelValue = 0,
    Section = 1,
    VitalSign = 2,
    DiagnosisList = 3,
    WhitespaceTable = 4,
    Regex = 5
}
