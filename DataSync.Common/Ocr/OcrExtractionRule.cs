namespace DataSync.Common.Ocr;

/// <summary>
/// OCR 字段提取规则。
/// </summary>
public sealed class OcrExtractionRule
{
    /// <summary>
    /// Ocr.ExtractedFields 中的输出字段名称。
    /// </summary>
    public string FieldCode { get; set; } = "";

    /// <summary>
    /// PDF 中需要查找的字段名称。旧配置为空时沿用 FieldCode。
    /// </summary>
    public string? SourceFieldName { get; set; }

    /// <summary>
    /// PDF 中字段所在的标题，用于区分表格内的同名字段。
    /// </summary>
    public string? SectionHeading { get; set; }

    public bool IsRequired { get; set; }

    /// <summary>
    /// 提供给本地 AI 的可选值提取范围说明，不能改变字段名称。
    /// </summary>
    public string? Instruction { get; set; }
}
