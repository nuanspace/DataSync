namespace DataSync.Common.Ocr;

/// <summary>
/// OCR 识别出的单个文本块。
/// </summary>
public sealed class OcrTextItem
{
    public int PageNumber { get; set; }

    public string Text { get; set; } = "";

    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public double Confidence { get; set; }
}
