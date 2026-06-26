namespace DataSync.Common.Ocr;

/// <summary>
/// OCR 运行环境参数。
/// </summary>
public sealed class OcrRuntimeOptions
{
    public string TesseractExecutable { get; set; } = "tesseract";

    public string PdfToPpmExecutable { get; set; } = "pdftoppm";

    public string TempRoot { get; set; } = "";

    public int UrlTimeoutSeconds { get; set; } = 30;

    public string? AllowedUrlHosts { get; set; }

    public string? AllowedUrlCidrs { get; set; }

    public string? AllowedOutputRoots { get; set; }
}
