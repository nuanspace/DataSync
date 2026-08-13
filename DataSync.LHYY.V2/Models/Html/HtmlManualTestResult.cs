namespace DataSync.LHYY.V2.Models.Html;

/// <summary>
/// HTML 手工测试批次结果，仅在当前页面内存中使用。
/// </summary>
public sealed class HtmlManualTestBatchResult
{
    public string InputMode { get; set; } = "";
    public List<HtmlManualTestItemResult> Items { get; } = [];
}

/// <summary>
/// HTML 手工测试中的单条记录结果。
/// </summary>
public sealed class HtmlManualTestItemResult
{
    public int RecordIndex { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public long ElapsedMs { get; set; }
    public HtmlExtractionResult? Extraction { get; set; }
}

/// <summary>
/// 从 HTML 手工测试结果中发现的可配置字段候选。
/// </summary>
public sealed class HtmlFieldCandidate
{
    public string FieldCode { get; set; } = "";
    public string? SourceSection { get; set; }
    public string? SourceLabel { get; set; }
    public Enums.HtmlExtractionType ExtractionType { get; set; }
    public string? PreviewValue { get; set; }
    public bool IsConfigured { get; set; }
    public bool IsSelected { get; set; }
}

/// <summary>
/// 将手工测试选中字段应用到接口配置页时传递的内存草稿。
/// </summary>
public sealed class HtmlManualTestApplyRequest
{
    public string Input { get; set; } = "";
    public List<HtmlExtractionRule> Rules { get; set; } = [];
    public HtmlManualTestBatchResult Result { get; set; } = new();
}
