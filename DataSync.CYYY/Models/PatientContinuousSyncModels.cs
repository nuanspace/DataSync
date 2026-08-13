using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.CYYY.Models;

public static class PatientContinuousSyncStatuses
{
    public const string WaitingData = "WaitingData";
    public const string Active = "Active";
    public const string Closing = "Closing";
    public const string Completed = "Completed";
}

public static class PatientContinuousInterfaceStatuses
{
    public const string Pending = "Pending";
    public const string Waiting = "Waiting";
    public const string Running = "Running";
    public const string Completed = "Completed";
}

/// <summary>
/// 一个任务下单次住院的持续同步档案。
/// </summary>
[Table("patient_continuous_sync_sessions", Schema = "cyyy")]
public class PatientContinuousSyncSession
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("task_id")]
    public int TaskId { get; set; }

    [Column("patient_id")]
    public string PatientId { get; set; } = "";

    [Column("visit_sn")]
    public string VisitSn { get; set; } = "";

    [Column("admission_time")]
    public DateTime? AdmissionTime { get; set; }

    [Column("discharge_time")]
    public DateTime? DischargeTime { get; set; }

    [Column("trigger_record_json")]
    public string TriggerRecordJson { get; set; } = "{}";

    [Column("status")]
    public string Status { get; set; } = PatientContinuousSyncStatuses.WaitingData;

    [Column("next_run_at")]
    public DateTime NextRunAt { get; set; } = DateTime.Now;

    [Column("last_error")]
    public string? LastError { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 单个患者档案在单个关联接口上的查询水位。
/// </summary>
[Table("patient_continuous_sync_interface_states", Schema = "cyyy")]
public class PatientContinuousSyncInterfaceState
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("session_id")]
    public long SessionId { get; set; }

    [Column("interface_id")]
    public int InterfaceId { get; set; }

    [Column("watermark")]
    public DateTime? Watermark { get; set; }

    [Column("next_run_at")]
    public DateTime NextRunAt { get; set; } = DateTime.Now;

    [Column("status")]
    public string Status { get; set; } = PatientContinuousInterfaceStatuses.Pending;

    [Column("retry_count")]
    public int RetryCount { get; set; }

    [Column("last_error")]
    public string? LastError { get; set; }

    [Column("last_started_at")]
    public DateTime? LastStartedAt { get; set; }

    [Column("last_success_at")]
    public DateTime? LastSuccessAt { get; set; }
}

/// <summary>
/// 已成功推送的新记录回执。
/// </summary>
[Table("patient_continuous_sync_receipts", Schema = "cyyy")]
public class PatientContinuousSyncReceipt
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("session_id")]
    public long SessionId { get; set; }

    [Column("interface_id")]
    public int InterfaceId { get; set; }

    [Column("record_key")]
    public string RecordKey { get; set; } = "";

    [Column("pushed_at")]
    public DateTime PushedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 患者持续同步运行日志。
/// </summary>
[Table("patient_continuous_sync_run_logs", Schema = "cyyy")]
public class PatientContinuousSyncRunLog
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("task_id")]
    public int TaskId { get; set; }

    [Column("session_id")]
    public long? SessionId { get; set; }

    [Column("interface_id")]
    public int? InterfaceId { get; set; }

    [Column("patient_id")]
    public string? PatientId { get; set; }

    [Column("visit_sn")]
    public string? VisitSn { get; set; }

    [Column("server_code")]
    public string? ServerCode { get; set; }

    [Column("interface_name")]
    public string? InterfaceName { get; set; }

    [Column("level")]
    public string Level { get; set; } = "Info";

    [Column("message")]
    public string Message { get; set; } = "";

    [Column("query_count")]
    public int QueryCount { get; set; }

    [Column("pushed_count")]
    public int PushedCount { get; set; }

    [Column("failed_count")]
    public int FailedCount { get; set; }

    [Column("window_from")]
    public DateTime? WindowFrom { get; set; }

    [Column("window_to")]
    public DateTime? WindowTo { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
