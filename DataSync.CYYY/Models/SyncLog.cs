using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.CYYY.Models;

/// <summary>
/// 推送日志
/// </summary>
[Table("sync_logs", Schema = "cyyy")]
public class SyncLog
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Column("task_code")]
    public string TaskCode { get; set; } = "";

    [Column("his_pat_id")]
    public string HisPatId { get; set; } = "";

    [Column("pat_visit_sn")]
    public string? PatVisitSn { get; set; }

    [Column("pat_name")]
    public string? PatName { get; set; }

    [Column("success")]
    public bool Success { get; set; }

    /// <summary>
    /// 接口编码（对应 SyncTaskInterface.ServerCode）
    /// </summary>
    [Column("server_code")]
    public string ServerCode { get; set; } = "";

    /// <summary>
    /// 接口名称（对应 SyncTaskInterface.DisplayName）
    /// </summary>
    [Column("interface_name")]
    public string InterfaceName { get; set; } = "";

    [Column("source_record_key")]
    public string? SourceRecordKey { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Scheduled / Backfill
    /// </summary>
    [Column("trigger_type")]
    public string TriggerType { get; set; } = "Scheduled";
}
