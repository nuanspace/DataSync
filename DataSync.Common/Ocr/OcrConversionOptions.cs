namespace DataSync.Common.Ocr;

/// <summary>
/// 单次 OCR 转换参数。
/// </summary>
public sealed class OcrConversionOptions
{
    public string Language { get; set; } = "chi_sim+eng";

    public int Dpi { get; set; } = 300;

    public int PageSegMode { get; set; } = 3;

    public int? MaxPages { get; set; }

    public long? MaxInputBytes { get; set; }

    public int TimeoutSeconds { get; set; } = 120;

    public bool KeepWorkFiles { get; set; }

    public bool IncludePreviewImages { get; set; }

    /// <summary>
    /// 内部版式识别开关，不向接口配置页面暴露。
    /// </summary>
    public bool IncludeLayoutRecognition { get; set; }

    /// <summary>
    /// 内部分批识别的起始页码，不向接口配置页面暴露。
    /// </summary>
    public int PageRangeStart { get; set; } = 1;

    /// <summary>
    /// 内部分批识别的页数，不向接口配置页面暴露。
    /// </summary>
    public int? PageRangeCount { get; set; }

    /// <summary>
    /// 样本分批预览时额外探测下一页是否存在。
    /// </summary>
    public bool ProbeNextPage { get; set; }

    public string? OutputJsonPath { get; set; }

    public string? OutputNameHint { get; set; }

    public IReadOnlyList<string> AllowedFileRoots { get; set; } = [];

    public IReadOnlyList<OcrExtractionRule> ExtractionRules { get; set; } = [];
}
