using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.LHYY.V2.Models.Entities;

/// <summary>
/// 平台侧事件身份映射
/// </summary>
[Table("esb_event_identity", Schema = "lhyy")]
public class EsbEventIdentity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("tran_code")]
    [MaxLength(20)]
    public string? TranCode { get; set; }

    [Column("integration_project_code")]
    [MaxLength(50)]
    public string? IntegrationProjectCode { get; set; }

    [Column("patient_id")]
    public Guid PatientId { get; set; }

    [Column("event_id")]
    public Guid EventId { get; set; }

    [Column("hospital_id")]
    public Guid HospitalId { get; set; }

    [Column("project_id")]
    public Guid ProjectId { get; set; }

    [Column("mrn")]
    [MaxLength(100)]
    public string Mrn { get; set; } = "";

    [Column("event_type_name")]
    [MaxLength(100)]
    public string EventTypeName { get; set; } = "";

    [Column("visit_no")]
    [MaxLength(100)]
    public string? VisitNo { get; set; }

    [Column("inpatient_no")]
    [MaxLength(100)]
    public string? InpatientNo { get; set; }

    [Column("event_start_time")]
    public DateTime? EventStartTime { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
