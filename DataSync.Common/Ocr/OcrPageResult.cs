namespace DataSync.Common.Ocr;

/// <summary>
/// 单页 OCR 结果。
/// </summary>
public sealed class OcrPageResult
{
    public int PageNumber { get; set; }

    public string Text { get; set; } = "";

    public IReadOnlyList<string> Lines { get; set; } = [];

    public IReadOnlyList<OcrTextItem> TextItems { get; set; } = [];

    public double MeanConfidence { get; set; }

    public string? RenderedImagePath { get; set; }
}
