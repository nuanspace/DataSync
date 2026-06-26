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

    public Dictionary<string, string> ExtractedFields { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
}
