using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DataSync.LHYY.V2.Models.Enums;

namespace DataSync.LHYY.V2.Models.Entities;

/// <summary>
/// 接口注册配置
/// </summary>
[Table("esb_interface_config", Schema = "lhyy")]
public class EsbInterfaceConfig
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("tran_code")]
    [MaxLength(20)]
    public string TranCode { get; set; } = "";

    [Column("integration_project_code")]
    [MaxLength(50)]
    public string? IntegrationProjectCode { get; set; }

    [Column("tran_name")]
    [MaxLength(100)]
    public string? TranName { get; set; }

    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    [Column("handler_type")]
    public HandlerType HandlerType { get; set; } = HandlerType.Generic;

    [Column("handler_name")]
    [MaxLength(200)]
    public string? HandlerName { get; set; }

    [Column("allow_multiple_match")]
    public bool AllowMultipleMatch { get; set; }

    [Column("receive_mode")]
    public ReceiveMode ReceiveMode { get; set; } = ReceiveMode.PersistAndAsync;

    [Column("license_code")]
    [MaxLength(100)]
    public string? LicenseCode { get; set; }

    [Column("event_type_name")]
    [MaxLength(100)]
    public string? EventTypeName { get; set; }

    [Column("description")]
    [MaxLength(500)]
    public string? Description { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    /// <summary>
    /// 病案号（MRN）在消息 JSON 中的路径，如 $.patient.mrn
    /// </summary>
    [Column("mrn_source_path")]
    [MaxLength(500)]
    public string? MrnSourcePath { get; set; }

    /// <summary>
    /// 事件开始时间在消息 JSON 中的路径，如 $.visit.admissionDate
    /// </summary>
    [Column("event_start_time_source_path")]
    [MaxLength(500)]
    public string? EventStartTimeSourcePath { get; set; }

    [Column("visit_no_source_path")]
    [MaxLength(500)]
    public string? VisitNoSourcePath { get; set; }

    [Column("inpatient_no_source_path")]
    [MaxLength(500)]
    public string? InpatientNoSourcePath { get; set; }

    [Column("source_message_id_path")]
    [MaxLength(500)]
    public string? SourceMessageIdPath { get; set; }

    [Column("main_record_array_path")]
    [MaxLength(500)]
    public string? MainRecordArrayPath { get; set; }

    [Column("allow_missing_event_time")]
    public bool AllowMissingEventTime { get; set; }

    [Column("response_mode")]
    public ApiResponseMode ResponseMode { get; set; } = ApiResponseMode.DefaultJson;

    [Column("missing_event_identity_policy")]
    public MissingEventIdentityPolicy MissingEventIdentityPolicy { get; set; } = MissingEventIdentityPolicy.Fail;

    [Column("sample_json")]
    public string? SampleJson { get; set; }
}
