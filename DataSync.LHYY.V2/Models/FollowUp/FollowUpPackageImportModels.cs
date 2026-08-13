using DataSync.Common.FollowUp;
using System.Text.Json;

namespace DataSync.LHYY.V2.Models.FollowUp;

public sealed class FollowUpPackageImportOptions
{
    public const string RequiredContractVersion = "followup-hospital-sync.v3";
    public const string CurrentImporterVersion = "1.2.0";

    public bool Enabled { get; set; }
    public string PackageRoot { get; set; } = "/app/followup/packages";
    public string StagingRoot { get; set; } = "/app/followup/staging";
    public string BackupRoot { get; set; } = "/app/followup/backups";
    public string AttachmentRoot { get; set; } = "/app/uploads";
    public string DecryptionPrivateKeyPath { get; set; } = "/app/secrets/followup_decryption_private.pem";
    public string CloudSigningPublicKeyPath { get; set; } = "/app/config/followup_signing_public.pem";
    public string EncryptionKeyId { get; set; } = string.Empty;
    public string SupportedContractVersion { get; set; } = RequiredContractVersion;
    public string ImporterVersion { get; set; } = CurrentImporterVersion;
    public string DeviceId { get; set; } = "datasync-device";
    public string HospitalId { get; set; } = string.Empty;
    public string HospitalCode { get; set; } = string.Empty;
    public string CyyyPrivateKeyPath { get; set; } = "/app/hospital-init/cyyy/cyyy_dmz_ed25519";
    public string CyyyKnownHostsPath { get; set; } = "/app/hospital-init/cyyy/dmz_known_hosts";
    public string CyyyTokenFilePath { get; set; } = "/app/hospital-init/cyyy/inner_device_token";
    public int CyyyFileOwnerUid { get; set; } = 1654;
    public int CyyyFileOwnerGid { get; set; } = 1654;
    public int ScanIntervalSeconds { get; set; } = 60;
    public long MaxPackageBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public long MaxExpandedBytes { get; set; } = 8L * 1024 * 1024 * 1024;
    public int MaxArchiveEntries { get; set; } = 100000;
    public int StorageWarningUsedPercent { get; set; } = 80;
    public int StorageCriticalUsedPercent { get; set; } = 90;
}

public sealed record FollowUpHospitalInitializationStatus(
    bool CyyyPrivateKeyReady,
    bool CyyyPublicKeyReady,
    bool LhyyPrivateKeyReady,
    bool LhyyPublicKeyReady,
    bool DmzKnownHostsReady,
    bool DmzTokenReady,
    bool CloudSigningKeyReady)
{
    public bool OutboundReady => CyyyPrivateKeyReady && CyyyPublicKeyReady && LhyyPrivateKeyReady && LhyyPublicKeyReady;
    public bool Complete => OutboundReady && DmzKnownHostsReady && DmzTokenReady && CloudSigningKeyReady;
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
    List<string> Messages,
    List<FollowUpTableColumnScope>? TableColumnScopes = null,
    List<FollowUpIgnoredColumnAudit>? IgnoredNonNullColumns = null);

public sealed class FollowUpTableColumnScope
{
    public FollowUpTableColumnScope(
        string sourceSchema,
        string sourceTable,
        string targetSchema,
        string targetTable,
        List<string> sourceColumns,
        List<string> targetColumns,
        List<string>? arrayToTextSourceColumns = null,
        List<string>? arrayToTextTargetColumns = null,
        List<string>? fileQuestionSourceColumns = null,
        List<string>? fileQuestionTargetColumns = null)
    {
        SourceSchema = sourceSchema;
        SourceTable = sourceTable;
        TargetSchema = targetSchema;
        TargetTable = targetTable;
        SourceColumns = sourceColumns;
        TargetColumns = targetColumns;
        ArrayToTextSourceColumns = arrayToTextSourceColumns ?? [];
        ArrayToTextTargetColumns = arrayToTextTargetColumns ?? [];
        FileQuestionSourceColumns = fileQuestionSourceColumns ?? [];
        FileQuestionTargetColumns = fileQuestionTargetColumns ?? [];
    }

    public FollowUpTableColumnScope(string schema, string table, List<string> columns)
        : this(schema, table, schema, table, columns.ToList(), columns.ToList())
    {
    }

    public string SourceSchema { get; }
    public string SourceTable { get; }
    public string TargetSchema { get; }
    public string TargetTable { get; }
    public List<string> SourceColumns { get; }
    public List<string> TargetColumns { get; }
    public List<string> ArrayToTextSourceColumns { get; }
    public List<string> ArrayToTextTargetColumns { get; }
    public List<string> FileQuestionSourceColumns { get; }
    public List<string> FileQuestionTargetColumns { get; }
}

public sealed record FollowUpIgnoredColumnAudit(
    string SourceSchema,
    string SourceTable,
    string ColumnName,
    int NonNullRowCount);

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
    long SizeBytes,
    string? AttachmentManifestHash = null,
    int? AttachmentEntryCount = null);

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
    public long? RecoveryBaselineSequenceNo { get; set; }
    public List<FollowUpStorageStatus> Storage { get; set; } = [];
    public List<FollowUpPackageImportState> Packages { get; set; } = [];
}

public sealed record FollowUpStorageCleanupBackup(
    Guid RecordId,
    string RootPath,
    string DatabaseBackupPath,
    string AttachmentBackupPath,
    string Hash,
    long SizeBytes,
    string? AttachmentManifestHash = null,
    int? AttachmentEntryCount = null);

public sealed record FollowUpStorageCleanupCandidate(
    string HospitalCode,
    string PackageId,
    long SequenceNo,
    string PackageHash,
    string PackagePath,
    IReadOnlyList<FollowUpStorageCleanupBackup> Backups);

public enum FollowUpStorageCleanupDatabaseState
{
    Original,
    Prepared,
    Archived,
    Inconsistent
}

public enum FollowUpStorageCleanupPhase
{
    Requested,
    DatabasePrepared,
    MovingFiles,
    FilesQuarantined,
    DatabaseArchived
}

public sealed class FollowUpStorageCleanupManifest
{
    public int Version { get; set; } = 1;
    public string OperationId { get; set; } = Guid.NewGuid().ToString("N");
    public string HospitalCode { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string OperatorName { get; set; } = string.Empty;
    public FollowUpStorageCleanupPhase Phase { get; set; }
    public FollowUpStorageCleanupCandidate? Candidate { get; set; }
    public List<FollowUpStorageCleanupManifestItem> Items { get; set; } = [];
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class FollowUpStorageCleanupManifestItem
{
    public string OriginalPath { get; set; } = string.Empty;
    public string QuarantinePath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
}
