namespace DataSync.CYYY.Models;

/// <summary>
/// 同步结果模型
/// </summary>
public class SyncResult
{
    public int SuccessCount;
    public int FailCount;
    public int SkipCount;
    public List<SyncFailDetail> FailDetails { get; set; } = [];
    public HashSet<string> CompletedInterfaceKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class SyncFailDetail
{
    public string HisPatId { get; set; } = "";
    public string? PatName { get; set; }
    public string TaskName { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
}

/// <summary>
/// 补录进度阶段
/// </summary>
public enum BackfillPhase
{
    TaskStart,     // 任务开始
    Ingested,      // 采集完成
    Filtered,      // 过滤完成
    SyncStart,     // 开始同步
    PatientDone,   // 单个患者完成
    TaskDone,      // 任务完成
    AllDone        // 全部完成
}

/// <summary>
/// 补录进度事件
/// </summary>
public class BackfillProgressEvent
{
    public string TaskCode { get; set; } = "";
    public string TaskName { get; set; } = "";
    public BackfillPhase Phase { get; set; }
    /// <summary>采集条数 or 过滤后条数</summary>
    public int? Count { get; set; }
    /// <summary>同步总数</summary>
    public int? Total { get; set; }
    /// <summary>已完成数</summary>
    public int? Completed { get; set; }
    public PatientSyncDetail? Patient { get; set; }
    /// <summary>是否跳过过滤步骤（按患者ID补录时为 true）</summary>
    public bool SkipFilter { get; set; }
}

/// <summary>
/// 单个患者的同步详情
/// </summary>
public class PatientSyncDetail
{
    public string HisPatId { get; set; } = "";
    public string? PatVisitSn { get; set; }
    public string? PatName { get; set; }
    public bool Success { get; set; }
    public bool Skipped { get; set; }
    public List<InterfaceSyncDetail> Interfaces { get; set; } = [];
}

/// <summary>
/// 单个接口的同步详情
/// </summary>
public class InterfaceSyncDetail
{
    public string ServerCode { get; set; } = "";
    public string InterfaceName { get; set; } = "";
    public bool Success { get; set; }
    public bool Skipped { get; set; }
    public string Stage { get; set; } = "";
    public string? ErrorMessage { get; set; }
}
