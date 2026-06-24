namespace DataSync.LHYY.V2.Models.Dto;

public class ActiveMedicalRecordListResponse
{
    public List<ActiveMedicalRecordItem> Items { get; set; } = [];

    public long? NextCursor { get; set; }
}

public class ActiveMedicalRecordItem
{
    public long Id { get; set; }

    public long Cursor => Id;

    public string Mrn { get; set; } = "";

    public string? InpatientNo { get; set; }

    public string? VisitNo { get; set; }

    public DateTime? AdmissionTime { get; set; }

    public Guid PatientId { get; set; }

    public Guid EventId { get; set; }
}
