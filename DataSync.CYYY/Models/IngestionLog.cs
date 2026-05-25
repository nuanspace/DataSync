using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.CYYY.Models;

/// <summary>
/// 采集日志
/// </summary>
[Table("ingestion_logs", Schema = "cyyy")]
public class IngestionLog
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Column("server_code")]
    public string ServerCode { get; set; } = "";

    [Column("source_name")]
    public string SourceName { get; set; } = "";

    [Column("trigger_type")]
    public string TriggerType { get; set; } = "Scheduled";

    [Column("time_field")]
    public string? TimeField { get; set; }

    [Column("from_time")]
    public DateTime? FromTime { get; set; }

    [Column("to_time")]
    public DateTime? ToTime { get; set; }

    [Column("query_conditions")]
    public string QueryConditions { get; set; } = "[]";

    [Column("api_count")]
    public int ApiCount { get; set; }

    [Column("local_count")]
    public int LocalCount { get; set; }

    [Column("success")]
    public bool Success { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("duration_ms")]
    public long DurationMs { get; set; }
}
