using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.CYYY.Models;

/// <summary>
/// 增量时间戳
/// </summary>
[Table("sync_checkpoints", Schema = "cyyy")]
public class SyncCheckpoint
{
    [Key]
    [Column("task_code")]
    public string TaskCode { get; set; } = "";

    [Column("last_success_time")]
    public DateTime LastSuccessTime { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>上次轮询查到的候选记录数</summary>
    [Column("last_poll_count")]
    public int LastPollCount { get; set; }

    /// <summary>上次实际推送时间（有数据推送时才更新）</summary>
    [Column("last_push_time")]
    public DateTime? LastPushTime { get; set; }

    /// <summary>上次推送成功数</summary>
    [Column("last_push_success")]
    public int LastPushSuccess { get; set; }

    /// <summary>上次推送失败数</summary>
    [Column("last_push_fail")]
    public int LastPushFail { get; set; }
}
