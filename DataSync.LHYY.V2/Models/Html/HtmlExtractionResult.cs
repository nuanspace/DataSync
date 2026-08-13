namespace DataSync.LHYY.V2.Models.Html;

/// <summary>
/// HTML 文本解析结果。字段值只允许为原文字符串或 null。
/// </summary>
public sealed class HtmlExtractionResult
{
    /// <summary>
    /// 仅供当前进程内的只读预览使用，正式消息输出和普通日志不得包含该值。
    /// </summary>
    public string CleanedText { get; set; } = "";
    public Dictionary<string, string> Sections { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string?> ExtractedFields { get; } = new(StringComparer.Ordinal);
    public List<string> MissingRequiredFields { get; } = [];
    public int TextLength { get; set; }
    public string EncodingName { get; set; } = "";
}
