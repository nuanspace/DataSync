using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.LHYY.V2.Models.Entities;

/// <summary>
/// 直处理消息的幂等回执
/// </summary>
[Table("esb_message_receipt", Schema = "lhyy")]
public class EsbMessageReceipt
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("tran_code")]
    [MaxLength(20)]
    public string TranCode { get; set; } = "";

    [Column("integration_project_code")]
    [MaxLength(50)]
    public string? IntegrationProjectCode { get; set; }

    [Column("source_message_id")]
    [MaxLength(100)]
    public string? SourceMessageId { get; set; }

    [Column("idempotent_key")]
    [MaxLength(200)]
    public string? IdempotentKey { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
