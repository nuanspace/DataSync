namespace DataSync.Common.Ocr;

/// <summary>
/// 单次 OCR 转换参数。
/// </summary>
public sealed class OcrConversionOptions
{
    public string Language { get; set; } = "chi_sim";

    public int Dpi { get; set; } = 300;

    public int PageSegMode { get; set; } = 11;

    public int? MaxPages { get; set; }

    public long? MaxInputBytes { get; set; }

    public int TimeoutSeconds { get; set; } = 120;

    public bool KeepWorkFiles { get; set; }

    public string? OutputJsonPath { get; set; }

    public string? OutputNameHint { get; set; }

    public IReadOnlyList<string> AllowedFileRoots { get; set; } = [];
}
