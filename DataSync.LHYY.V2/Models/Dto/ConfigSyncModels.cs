using DataSync.LHYY.V2.Models.Enums;

namespace DataSync.LHYY.V2.Models.Dto;

public sealed class ConfigSyncExportOptions
{
    public bool IncludeInterfaces { get; set; } = true;
    public bool IncludeMappings { get; set; } = true;
    public bool IncludeDictionaries { get; set; } = true;
    public bool IncludeProjectConfigs { get; set; }
}

public sealed class ConfigSyncPackage
{
    public string PackageType { get; set; } = ConfigSyncConstants.PackageType;
    public int Version { get; set; } = 1;
    public string PackageId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime ExportedAt { get; set; } = DateTime.Now;
    public string SourceProjectCode { get; set; } = "";
    public List<ConfigSyncInterfaceConfig> Interfaces { get; set; } = [];
    public List<ConfigSyncFieldMapping> FieldMappings { get; set; } = [];
    public List<ConfigSyncDictEntry> DictEntries { get; set; } = [];
    public List<ConfigSyncProjectConfig> ProjectConfigs { get; set; } = [];
}

public static class ConfigSyncConstants
{
    public const string PackageType = "DataSync.LHYY.V2.ConfigSync";
}

public sealed class ConfigSyncInterfaceConfig
{
    public string TranCode { get; set; } = "";
    public string? TranName { get; set; }
    public bool IsEnabled { get; set; } = true;
    public HandlerType HandlerType { get; set; } = HandlerType.Generic;
    public string? HandlerName { get; set; }
    public bool AllowMultipleMatch { get; set; }
    public ReceiveMode ReceiveMode { get; set; } = ReceiveMode.PersistAndAsync;
    public string? LicenseCode { get; set; }
    public string? EventTypeName { get; set; }
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public string? MrnSourcePath { get; set; }
    public string? EventStartTimeSourcePath { get; set; }
    public string? VisitNoSourcePath { get; set; }
    public string? InpatientNoSourcePath { get; set; }
    public string? CombinedVisitIdentitySourcePath { get; set; }
    public CombinedVisitIdentityFormat CombinedVisitIdentityFormat { get; set; }
    public string? SourceMessageIdPath { get; set; }
    public string? MainRecordArrayPath { get; set; }
    public bool AllowMissingEventTime { get; set; }
    public ApiResponseMode ResponseMode { get; set; } = ApiResponseMode.DefaultJson;
    public MissingEventIdentityPolicy MissingEventIdentityPolicy { get; set; } = MissingEventIdentityPolicy.Fail;
    public MedicalRecordSyncRole MedicalRecordSyncRole { get; set; } = MedicalRecordSyncRole.None;
    public bool SoapEnabled { get; set; }
    public string? SoapServiceCode { get; set; }
    public string? SoapOperation { get; set; }
    public string? SoapAction { get; set; }
    public string? SampleJson { get; set; }
    public List<ConfigSyncFilterRule> FilterRules { get; set; } = [];
    public List<ConfigSyncInterfaceMatchRule> MatchRules { get; set; } = [];
    public List<ConfigSyncIdempotentKeyPart> IdempotentKeyParts { get; set; } = [];
    public string ContentHash { get; set; } = "";
}

public sealed class ConfigSyncFieldMapping
{
    public string SyncKey { get; set; } = "";
    public string TranCode { get; set; } = "";
    public MappingTarget MappingTarget { get; set; }
    public string SourcePath { get; set; } = "";
    public string TargetField { get; set; } = "";
    public string? TargetFieldName { get; set; }
    public Guid? CardId { get; set; }
    public string? CardName { get; set; }
    public string? ArrayPath { get; set; }
    public string? DictCode { get; set; }
    public string DictMatchMode { get; set; } = "";
    public string? DefaultValue { get; set; }
    public string? ValueExpression { get; set; }
    public bool IsRequired { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
    public string? Description { get; set; }
    public List<ConfigSyncFilterRule> FilterRules { get; set; } = [];
    public string ContentHash { get; set; } = "";
}

public sealed class ConfigSyncFilterRule
{
    public string SourcePath { get; set; } = "";
    public string Operator { get; set; } = "eq";
    public string CompareValue { get; set; } = "";
    public int RuleGroup { get; set; } = 1;
    public FilterScope FilterScope { get; set; } = FilterScope.MessageCheck;
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
    public string? Description { get; set; }
}

public sealed class ConfigSyncInterfaceMatchRule
{
    public int MatchGroup { get; set; } = 1;
    public string SourcePath { get; set; } = "";
    public string Operator { get; set; } = "eq";
    public string CompareValue { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
    public string? Description { get; set; }
}

public sealed class ConfigSyncIdempotentKeyPart
{
    public string? SourcePath { get; set; }
    public string? LiteralValue { get; set; }
    public string? DefaultValue { get; set; }
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? Description { get; set; }
}

public sealed class ConfigSyncDictEntry
{
    public string DictCode { get; set; } = "";
    public string SourceValue { get; set; } = "";
    public string TargetValue { get; set; } = "";
    public int SortOrder { get; set; }
    public string ContentHash { get; set; } = "";
}

public sealed class ConfigSyncProjectConfig
{
    public string ConfigKey { get; set; } = "";
    public string? ConfigValue { get; set; }
    public string? ConfigType { get; set; }
    public string? Description { get; set; }
    public string ContentHash { get; set; } = "";
}

public sealed class ConfigSyncPreviewResult
{
    public string PackageId { get; set; } = "";
    public string SourceProjectCode { get; set; } = "";
    public DateTime ExportedAt { get; set; }
    public string TargetProjectCode { get; set; } = "";
    public List<ConfigSyncPreviewItem> Items { get; set; } = [];
}

public sealed class ConfigSyncPreviewItem
{
    public string ItemKey { get; set; } = "";
    public ConfigSyncItemType ItemType { get; set; }
    public ConfigSyncChangeType ChangeType { get; set; }
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public string LocalSummary { get; set; } = "";
    public string PackageSummary { get; set; } = "";
    public bool IsSelected { get; set; }
    public bool CanApply => ChangeType != ConfigSyncChangeType.Identical;
}

public sealed class ConfigSyncApplyResult
{
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public List<string> Messages { get; set; } = [];
}

public enum ConfigSyncItemType
{
    Interface,
    FieldMapping,
    Dictionary,
    ProjectConfig
}

public enum ConfigSyncChangeType
{
    New,
    Identical,
    SafeUpdate,
    Conflict
}
