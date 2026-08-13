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

    public int ImageWidth { get; set; }

    public int ImageHeight { get; set; }

    [System.Runtime.Serialization.IgnoreDataMember]
    [System.Text.Json.Serialization.JsonIgnore]
    public string? PreviewImageDataUrl { get; set; }

    [System.Runtime.Serialization.IgnoreDataMember]
    [System.Text.Json.Serialization.JsonIgnore]
    public int PreviewSourceWidth { get; set; }

    [System.Runtime.Serialization.IgnoreDataMember]
    [System.Text.Json.Serialization.JsonIgnore]
    public int PreviewSourceHeight { get; set; }
}
