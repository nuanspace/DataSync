namespace DataSync.Common.Ocr;

/// <summary>
/// PDF OCR 转换结果。
/// </summary>
public sealed class OcrDocumentResult
{
    public OcrSourceKind SourceKind { get; set; }

    public string Language { get; set; } = "";

    public int Dpi { get; set; }

    public int PageSegMode { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public int PageCount { get; set; }

    public string FullText { get; set; } = "";

    public IReadOnlyList<OcrPageResult> Pages { get; set; } = [];

    public IReadOnlyList<OcrTextItem> TextItems { get; set; } = [];

    /// <summary>
    /// 仅供按所属标题提取字段使用，不进入正式输出。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<OcrPageResult> LayoutPages { get; set; } = [];

    /// <summary>
    /// 仅供样本分批预览判断是否还能加载下一批页面。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasMorePages { get; set; }

    public Dictionary<string, string?> ExtractedFields { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}
