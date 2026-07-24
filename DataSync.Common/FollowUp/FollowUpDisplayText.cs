namespace DataSync.Common.FollowUp;

public static class FollowUpDisplayText
{
    public static string PackageType(string? value) => value switch
    {
        "Baseline" => "基础包",
        "Incremental" => "增量包",
        "Supplement" => "补充包",
        "Replacement" => "替代包",
        _ => "未知类型"
    };

    public static string SchemaDiff(string? value) => value switch
    {
        null or "" => "未检查",
        "Compatible" => "兼容",
        "Additive" => "仅新增",
        "RequiresMapping" => "需要映射",
        "Breaking" => "不兼容",
        _ => "未知"
    };

    public static string PullStatus(string? value) => value switch
    {
        "Pending" => "等待拉取",
        "Pulling" => "正在拉取",
        "Pulled" => "已拉取",
        "Archiving" => "正在清理",
        "Archived" => "文件已清理",
        "Failed" => "拉取失败",
        _ => "未知状态"
    };

    public static string ImportStatus(string? value) => value switch
    {
        "AwaitingPackage" => "等待包文件",
        "Pending" => "等待处理",
        "Validating" => "正在校验",
        "WaitingForDecision" => "等待结构处理",
        "WaitingForPredecessor" => "等待前驱包",
        "RejectedSchemaMismatch" => "结构不兼容",
        "BackingUp" => "正在备份",
        "Importing" => "正在导入",
        "Imported" => "已导入",
        "ImportFailed" => "导入失败",
        "RestoreRequired" => "需要恢复",
        "Restoring" => "正在恢复",
        "Restored" => "已恢复",
        "RestoreFailed" => "恢复失败",
        _ => "未知状态"
    };

    public static string AckStatus(string? value) => value switch
    {
        "Imported" => "导入成功",
        "ImportFailed" => "导入失败",
        "RejectedSchemaMismatch" => "结构不兼容",
        "Restored" => "已恢复",
        _ => "未知结果"
    };

    public static string ForwardStatus(string? value) => value switch
    {
        "Pending" => "等待转发",
        "Forwarding" => "正在转发",
        "Forwarded" => "已转发",
        "Failed" => "转发失败",
        _ => "未知状态"
    };

    public static string TriggerType(string? value) => value switch
    {
        "Scheduled" => "定时生成",
        "Manual" => "手工生成",
        "RecoveryBaseline" => "恢复基线",
        _ => "未知方式"
    };

    public static bool CanImport(string? importStatus) => importStatus is
        "Pending"
        or "WaitingForPredecessor"
        or "WaitingForDecision"
        or "RejectedSchemaMismatch"
        or "ImportFailed"
        or "Restored";

    public static bool CanStartImport(string? importStatus, bool hasUnsafeOperation) =>
        !hasUnsafeOperation && CanImport(importStatus);

    public static bool CanRestore(string? importStatus) => importStatus is
        "Imported"
        or "RestoreFailed"
        or "Importing"
        or "Restoring";

    public static int PullIntervalMinutes(int seconds) => Math.Max(1, (int)Math.Ceiling(seconds / 60d));

    public static int PullIntervalSeconds(int minutes) => Math.Clamp(minutes, 1, 1440) * 60;

    public static string PullScheduleDescription(int seconds) =>
        $"服务每 30 秒扫描一次；达到 {PullIntervalMinutes(seconds)} 分钟滚动间隔后执行，实际时间受扫描周期及其他拉取任务耗时影响。";

    public static string SourceScheduleStatus(bool globalEnabled, bool sourceEnabled) =>
        (globalEnabled, sourceEnabled) switch
        {
            (true, true) => "定时拉取已启用",
            (false, true) => "计划已保存，自动拉取服务未启用",
            _ => "仅手工拉取"
        };
}
