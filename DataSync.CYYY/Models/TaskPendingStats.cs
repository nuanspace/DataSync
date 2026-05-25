namespace DataSync.CYYY.Models;

/// <summary>
/// 任务待同步对象统计
/// </summary>
public class TaskPendingStats
{
    public string TaskCode { get; set; } = "";

    public int PendingCount { get; set; }

    public int WaitingCount { get; set; }

    public int FailedCount { get; set; }

    public int RunningCount { get; set; }

    public int SuccessCount { get; set; }

    public int SkippedCount { get; set; }

    public DateTime? LastUpdatedAt { get; set; }

    public int ActiveCount => PendingCount + WaitingCount + FailedCount + RunningCount;
}
