using System.Text.Json;

namespace DataSync.Common.FollowUp;

public sealed class FollowUpPackageManifest
{
    public string HospitalCode { get; set; } = string.Empty;
    public Guid HospitalId { get; set; }
    public string PackageId { get; set; } = string.Empty;
    public long SequenceNo { get; set; }
    public string PackageType { get; set; } = string.Empty;
    public DateTime? FromWatermark { get; set; }
    public DateTime ToWatermark { get; set; }
    public string? PreviousPackageId { get; set; }
    public string? RelatedPackageId { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public string ExportContractVersion { get; set; } = string.Empty;
    public string MinImporterVersion { get; set; } = string.Empty;
    public string SourceDbFingerprint { get; set; } = string.Empty;
    public string SchemaSnapshotHash { get; set; } = string.Empty;
    public string TableManifestHash { get; set; } = string.Empty;
    public string SchemaDiffHash { get; set; } = string.Empty;
    public List<FollowUpDataFileManifest> DataFiles { get; set; } = [];
    public List<FollowUpAttachmentManifest> AttachmentFiles { get; set; } = [];
    public Dictionary<string, int> RecordCounts { get; set; } = [];
}

public sealed class FollowUpDataFileManifest
{
    public string Path { get; set; } = string.Empty;
    public string Table { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public int RecordCount { get; set; }
}

public sealed class FollowUpAttachmentManifest
{
    public string Path { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

public sealed class FollowUpTableManifestItem
{
    public string Schema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool Required { get; set; }
    public string DataCategory { get; set; } = string.Empty;
    public string ImportPolicy { get; set; } = string.Empty;
    public JsonElement Dependencies { get; set; }
    public JsonElement Increment { get; set; }
    public List<string> PrimaryKey { get; set; } = [];
    public string? WatermarkColumn { get; set; }
    public bool HasIncrementalData { get; set; }
    public string? ExportPath { get; set; }
    public string? SchemaHash { get; set; }
    public int RecordCount { get; set; }
    public string? FileHash { get; set; }
    public string? ContentHash { get; set; }
    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }
}

public sealed class FollowUpSchemaSnapshot
{
    public string ExportContractVersion { get; set; } = string.Empty;
    public string SourceDbFingerprint { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; }
    public List<FollowUpTableSchema> Tables { get; set; } = [];
}

public sealed class FollowUpTableSchema
{
    public string SchemaName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string SchemaHash { get; set; } = string.Empty;
    public List<FollowUpColumnSchema> Columns { get; set; } = [];
    public List<string> PrimaryKey { get; set; } = [];
    public List<string> UniqueConstraints { get; set; } = [];
    public List<string> Indexes { get; set; } = [];
}

public sealed class FollowUpColumnSchema
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public string? DefaultValue { get; set; }
    public int OrdinalPosition { get; set; }
}

public sealed class FollowUpSchemaDiff
{
    public string DiffLevel { get; set; } = "Compatible";
    public string Recommendation { get; set; } = string.Empty;
    public string SnapshotHash { get; set; } = string.Empty;
    public List<string> AddedTables { get; set; } = [];
    public List<string> RemovedTables { get; set; } = [];
    public List<string> ChangedColumns { get; set; } = [];
    public List<string> ChangedConstraints { get; set; } = [];
}
