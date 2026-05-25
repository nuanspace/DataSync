using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DataSync.LHYY.V2.Models.Enums;

namespace DataSync.LHYY.V2.Models.Entities;

/// <summary>
/// ESB 消息日志（含处理状态、重试计数）
/// </summary>
[Table("esb_messages", Schema = "lhyy")]
public class EsbMessage
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("message_id")]
    [MaxLength(100)]
    public string MessageId { get; set; } = "";

    [Column("source_message_id")]
    [MaxLength(100)]
    public string? SourceMessageId { get; set; }

    [Column("tran_code")]
    [MaxLength(20)]
    public string TranCode { get; set; } = "";

    [Column("integration_project_code")]
    [MaxLength(50)]
    public string? IntegrationProjectCode { get; set; }

    [Column("tran_name")]
    [MaxLength(100)]
    public string? TranName { get; set; }

    [Column("app_id")]
    [MaxLength(50)]
    public string? AppId { get; set; }

    [Column("org_id")]
    [MaxLength(50)]
    public string? OrgId { get; set; }

    [Column("esb_timestamp")]
    [MaxLength(50)]
    public string? EsbTimestamp { get; set; }

    [Column("raw_json", TypeName = "jsonb")]
    public string RawJson { get; set; } = "{}";

    [Column("body_json", TypeName = "jsonb")]
    public string? BodyJson { get; set; }

    [Column("idempotent_key")]
    [MaxLength(200)]
    public string? IdempotentKey { get; set; }

    [Column("mrn")]
    [MaxLength(100)]
    public string? Mrn { get; set; }

    [Column("visit_no")]
    [MaxLength(100)]
    public string? VisitNo { get; set; }

    [Column("inpatient_no")]
    [MaxLength(100)]
    public string? InpatientNo { get; set; }

    [Column("resolved_event_time")]
    public DateTime? ResolvedEventTime { get; set; }

    [Column("matched_rule_group")]
    public int? MatchedRuleGroup { get; set; }

    [Column("status")]
    public MessageStatus Status { get; set; } = MessageStatus.Pending;

    [Column("retry_count")]
    public int RetryCount { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("patient_id")]
    public Guid? PatientId { get; set; }

    [Column("event_id")]
    public Guid? EventId { get; set; }

    [Column("processed_at")]
    public DateTime? ProcessedAt { get; set; }

    [Column("processing_started_at")]
    public DateTime? ProcessingStartedAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
