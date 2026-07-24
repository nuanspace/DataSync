using DataSync.Common.FollowUp;

namespace DataSync.CYYY.Models.FollowUp;

public sealed class FollowUpPackageSyncOptions
{
    public bool Enabled { get; set; }
    public string PrivateKeyPath { get; set; } = "/app/secrets/followup_dmz_ed25519";
    public string KnownHostsPath { get; set; } = "/app/config/followup_dmz_known_hosts";
    public string TokenFilePath { get; set; } = "/app/secrets/followup_dmz_token";
    public string DeviceId { get; set; } = "datasync-device";
    public int RequestWindowSeconds { get; set; } = 300;
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int ListLimit { get; set; } = 100;
    public long MaxPackageBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public int AckRetrySeconds { get; set; } = 60;
    public int StorageWarningUsedPercent { get; set; } = 80;
    public int StorageCriticalUsedPercent { get; set; } = 90;
}

public sealed class FollowUpPackageSourceConfig
{
    public Guid Id { get; set; }
    public string HospitalCode { get; set; } = string.Empty;
    public string HospitalName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string DmzHost { get; set; } = string.Empty;
    public int DmzPort { get; set; } = 22;
    public string DmzUser { get; set; } = string.Empty;
    public string PackageRoot { get; set; } = string.Empty;
    public int PullIntervalSeconds { get; set; } = 300;
    public string PullPolicyJson { get; set; } = "{}";
    public string SecurityJson { get; set; } = "{}";
}

public sealed class FollowUpPackagePullState
{
    public Guid Id { get; set; }
    public string HospitalCode { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public long SequenceNo { get; set; }
    public string PackageType { get; set; } = string.Empty;
    public string TriggerType { get; set; } = string.Empty;
    public string PullStatus { get; set; } = string.Empty;
    public DateTime? FromWatermark { get; set; }
    public DateTime? ToWatermark { get; set; }
    public string? PreviousPackageId { get; set; }
    public string? PackageHash { get; set; }
    public long SizeBytes { get; set; }
    public string LocalPackagePath { get; set; } = string.Empty;
    public string SchemaSummaryJson { get; set; } = "{}";
    public string PackageSummaryJson { get; set; } = "{}";
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
    public DateTime? LastPulledAt { get; set; }
}

public sealed class FollowUpPackageAckQueueItem
{
    public Guid Id { get; set; }
    public string HospitalCode { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string AckStatus { get; set; } = string.Empty;
    public string AckPayloadJson { get; set; } = "{}";
    public string ForwardStatus { get; set; } = string.Empty;
    public int RetryCount { get; set; }
}

public sealed record FollowUpOperationResult(bool Success, string Message, string? ErrorCode = null);

public sealed class FollowUpPackageSyncOverview
{
    public bool TablesReady { get; set; }
    public List<string> MissingTables { get; set; } = [];
    public List<FollowUpPackageSourceConfig> Sources { get; set; } = [];
    public List<FollowUpPackagePullState> Packages { get; set; } = [];
    public List<FollowUpPackageAckQueueItem> Acks { get; set; } = [];
    public List<FollowUpStorageStatus> Storage { get; set; } = [];

    public IEnumerable<FollowUpPackagePullState> PackagesFor(string? hospitalCode) =>
        string.IsNullOrWhiteSpace(hospitalCode)
            ? []
            : Packages.Where(item => item.HospitalCode == hospitalCode);

    public IEnumerable<FollowUpPackageAckQueueItem> AcksFor(string? hospitalCode) =>
        string.IsNullOrWhiteSpace(hospitalCode)
            ? []
            : Acks.Where(item => item.HospitalCode == hospitalCode);
}
