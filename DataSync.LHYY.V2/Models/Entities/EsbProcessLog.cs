using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.LHYY.V2.Models.Entities;

/// <summary>
/// 处理日志
/// </summary>
[Table("esb_process_log", Schema = "lhyy")]
public class EsbProcessLog
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("message_id")]
    public long MessageId { get; set; }

    [Column("integration_project_code")]
    [MaxLength(50)]
    public string? IntegrationProjectCode { get; set; }

    [Column("step")]
    [MaxLength(100)]
    public string Step { get; set; } = "";

    [Column("is_success")]
    public bool IsSuccess { get; set; }

    [Column("detail")]
    public string? Detail { get; set; }

    [Column("elapsed_ms")]
    public int ElapsedMs { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
