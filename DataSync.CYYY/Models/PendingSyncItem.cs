using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.CYYY.Models;

/// <summary>
/// 待同步对象状态
/// </summary>
public static class PendingSyncStatuses
{
    public const string Pending = "Pending";
    public const string Waiting = "Waiting";
    public const string Running = "Running";
    public const string Success = "Success";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}

/// <summary>
/// 待同步对象清单
/// </summary>
[Table("pending_sync_items", Schema = "cyyy")]
public class PendingSyncItem
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("task_code")]
    public string TaskCode { get; set; } = "";

    [Column("source_server_code")]
    public string SourceServerCode { get; set; } = "";

    [Column("source_record_key")]
    public string SourceRecordKey { get; set; } = "";

    [Column("object_key")]
    public string ObjectKey { get; set; } = "";

    [Column("his_pat_id")]
    public string HisPatId { get; set; } = "";

    [Column("pat_visit_sn")]
    public string PatVisitSn { get; set; } = "";

    [Column("pat_name")]
    public string PatName { get; set; } = "";

    [Column("trigger_record_json")]
    public string TriggerRecordJson { get; set; } = "{}";

    [Column("trigger_push_done")]
    public bool TriggerPushDone { get; set; }

    [Column("trigger_push_done_at")]
    public DateTime? TriggerPushDoneAt { get; set; }

    [Column("trigger_push_error")]
    public string? TriggerPushError { get; set; }

    [Column("status")]
    public string Status { get; set; } = PendingSyncStatuses.Pending;

    [Column("retry_count")]
    public int RetryCount { get; set; }

    [Column("last_error")]
    public string? LastError { get; set; }

    [Column("next_retry_time")]
    public DateTime? NextRetryTime { get; set; }

    [Column("last_started_at")]
    public DateTime? LastStartedAt { get; set; }

    [Column("last_completed_at")]
    public DateTime? LastCompletedAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
