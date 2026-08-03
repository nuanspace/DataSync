using DataSync.LHYY.V2.Models.Enums;

namespace DataSync.LHYY.V2.Models.Dto;

/// <summary>
/// 批量重试提交前的预检结果。
/// </summary>
public class BatchRetryPreview
{
    public int SelectedCount { get; set; }
    public int ValidCount { get; set; }
    public int HotCount { get; set; }
    public int ArchiveCount { get; set; }
    public Dictionary<MessageStatus, int> StatusCounts { get; set; } = [];
    public List<BatchRetrySkippedItem> SkippedItems { get; set; } = [];
}

/// <summary>
/// 批量重试提交结果。
/// </summary>
public class BatchRetryResult
{
    public int SubmittedCount { get; set; }
    public int RestoredArchiveCount { get; set; }
    public List<BatchRetrySkippedItem> SkippedItems { get; set; } = [];
}

/// <summary>
/// 未参与批量重试的消息及原因。
/// </summary>
public class BatchRetrySkippedItem
{
    public long Id { get; set; }
    public string? MessageId { get; set; }
    public string Reason { get; set; } = "";
}
