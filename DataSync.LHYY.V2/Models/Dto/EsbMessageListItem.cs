using System.ComponentModel.DataAnnotations.Schema;
using DataSync.LHYY.V2.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace DataSync.LHYY.V2.Models.Dto;

/// <summary>
/// 消息列表轻量行，不包含 raw_json/body_json 大字段。
/// </summary>
[Keyless]
public class EsbMessageListItem
{
    [Column("id")]
    public long Id { get; set; }

    [Column("message_id")]
    public string MessageId { get; set; } = "";

    [Column("tran_code")]
    public string TranCode { get; set; } = "";

    [Column("integration_project_code")]
    public string? IntegrationProjectCode { get; set; }

    [Column("tran_name")]
    public string? TranName { get; set; }

    [Column("mrn")]
    public string? Mrn { get; set; }

    [Column("resolved_event_time")]
    public DateTime? ResolvedEventTime { get; set; }

    [Column("status")]
    public MessageStatus Status { get; set; }

    [Column("retry_count")]
    public int RetryCount { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
