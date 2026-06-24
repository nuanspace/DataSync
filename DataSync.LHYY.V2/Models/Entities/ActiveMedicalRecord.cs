using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.LHYY.V2.Models.Entities;

public static class ActiveMedicalRecordStatuses
{
    public const string Active = "Active";
    public const string Finished = "Finished";
}

/// <summary>
/// LHYY 侧维护的 Active 病历清单。
/// </summary>
[Table("active_medical_records", Schema = "lhyy")]
public class ActiveMedicalRecord
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("integration_project_code")]
    [MaxLength(50)]
    public string? IntegrationProjectCode { get; set; }

    [Column("tran_code")]
    [MaxLength(20)]
    public string? TranCode { get; set; }

    [Column("mrn")]
    [MaxLength(100)]
    public string Mrn { get; set; } = "";

    [Column("inpatient_no")]
    [MaxLength(100)]
    public string? InpatientNo { get; set; }

    [Column("visit_no")]
    [MaxLength(100)]
    public string? VisitNo { get; set; }

    [Column("patient_id")]
    public Guid PatientId { get; set; }

    [Column("event_id")]
    public Guid EventId { get; set; }

    [Column("event_type_name")]
    [MaxLength(100)]
    public string EventTypeName { get; set; } = "";

    [Column("admission_time")]
    public DateTime? AdmissionTime { get; set; }

    [Column("discharge_time")]
    public DateTime? DischargeTime { get; set; }

    [Column("status")]
    [MaxLength(20)]
    public string Status { get; set; } = ActiveMedicalRecordStatuses.Active;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    [Column("finished_at")]
    public DateTime? FinishedAt { get; set; }
}
