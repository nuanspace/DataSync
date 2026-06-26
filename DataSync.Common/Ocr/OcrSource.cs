namespace DataSync.Common.Ocr;

/// <summary>
/// OCR 输入来源。
/// </summary>
public sealed class OcrSource
{
    public OcrSourceKind Kind { get; set; } = OcrSourceKind.FilePath;

    public string Value { get; set; } = "";
}
