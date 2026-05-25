namespace DataSync.LHYY.V2.Models.Dto;

/// <summary>
/// 请求级处理日志明细
/// </summary>
public class RequestProcessLogDetail
{
    public bool IsBatch { get; set; }
    public int Queued { get; set; }
    public int Processed { get; set; }
    public int Filtered { get; set; }
    public int Duplicated { get; set; }
    public int Failed { get; set; }
    public int Unmatched { get; set; }
    public List<string> MatchedTranCodes { get; set; } = [];
    public List<RequestProcessItemDetail> Items { get; set; } = [];
    public List<ProcessStepInfo> Steps { get; set; } = [];
}

/// <summary>
/// 请求内单项处理结果
/// </summary>
public class RequestProcessItemDetail
{
    public string? RecordIndex { get; set; }
    public string? TranCode { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public List<ProcessStepInfo> Steps { get; set; } = [];
}
