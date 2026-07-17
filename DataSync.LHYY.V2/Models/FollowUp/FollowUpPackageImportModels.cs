using DataSync.Common.FollowUp;
using System.Text.Json;

namespace DataSync.LHYY.V2.Models.FollowUp;

public sealed class FollowUpPackageImportOptions
{
    public bool Enabled { get; set; }
    public string PackageRoot { get; set; } = "/app/followup/packages";
    public string StagingRoot { get; set; } = "/app/followup/staging";
    public string BackupRoot { get; set; } = "/app/followup/backups";
    public string AttachmentRoot { get; set; } = "/app/uploads";
    public string DecryptionPrivateKeyPath { get; set; } = "/app/secrets/followup_decryption_private.pem";
    public string CloudSigningPublicKeyPath { get; set; } = "/app/config/followup_signing_public.pem";
    public string EncryptionKeyId { get; set; } = string.Empty;
    public string SupportedContractVersion { get; set; } = "1.0";
    public string ImporterVersion { get; set; } = "1.0.0";
    public string DeviceId { get; set; } = "datasync-device";
    public int ScanIntervalSeconds { get; set; } = 60;
    public long MaxPackageBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public long MaxExpandedBytes { get; set; } = 8L * 1024 * 1024 * 1024;
    public int MaxArchiveEntries { get; set; } = 100000;
}

public sealed record FollowUpVerifiedPackage(
    string PackagePath,
    string PackageHash,
    string StagingPath,
    FollowUpEncryptedEnvelope Envelope,
    FollowUpPackageManifest Manifest,
    List<FollowUpTableManifestItem> TableManifest,
    FollowUpSchemaSnapshot SchemaSnapshot,
    FollowUpSchemaDiff SchemaDiff);

public sealed class FollowUpPackageImportState
{
    public Guid Id { get; set; }
    public string HospitalCode { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public long SequenceNo { get; set; }
    public string PackageType { get; set; } = string.Empty;
    public string ImportStatus { get; set; } = string.Empty;
    public string? PreviousPackageId { get; set; }
    public string PackageHash { get; set; } = string.Empty;
    public string LocalPackagePath { get; set; } = string.Empty;
    public string? StagingPath { get; set; }
    public string? SchemaDiffLevel { get; set; }
    public bool RequiresSchemaReview { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

public sealed record FollowUpSchemaCheckResult(
    string Status,
    string DiffLevel,
    bool Compatible,
    List<string> Messages);

public sealed record FollowUpImportOperationResult(bool Success, string Message, string? ErrorCode = null);

public sealed class FollowUpSchemaDecision
{
    public string DecisionStatus { get; set; } = "WaitingForUpgrade";
    public string OperatorName { get; set; } = string.Empty;
    public Dictionary<string, FollowUpTableMapping> TableMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class FollowUpTableMapping
{
    public string? TargetSchema { get; set; }
    public string? TargetTable { get; set; }
    public Dictionary<string, string> ColumnMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, JsonElement> DefaultValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class FollowUpDiscoveredPackage
{
    public string HospitalCode { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public long SequenceNo { get; set; }
    public string PackageType { get; set; } = string.Empty;
    public DateTime? FromWatermark { get; set; }
    public DateTime? ToWatermark { get; set; }
    public string? PreviousPackageId { get; set; }
    public string? PackageHash { get; set; }
    public string LocalPackagePath { get; set; } = string.Empty;
    public string PullStatus { get; set; } = string.Empty;
}

public sealed record FollowUpBackupArtifact(
    Guid RecordId,
    string RootPath,
    string DatabaseBackupPath,
    string AttachmentBackupPath,
    string Hash,
    long SizeBytes);

public sealed class FollowUpPackageImportOverview
{
    public bool TablesReady { get; set; }
    public List<string> MissingTables { get; set; } = [];
    public bool PackageRootReady { get; set; }
    public bool StagingReady { get; set; }
    public bool BackupReady { get; set; }
    public bool AttachmentRootReady { get; set; }
    public bool DecryptionKeyReady { get; set; }
    public bool SigningKeyReady { get; set; }
    public bool PostgreSqlToolsReady { get; set; }
    public List<FollowUpPackageImportState> Packages { get; set; } = [];
}
