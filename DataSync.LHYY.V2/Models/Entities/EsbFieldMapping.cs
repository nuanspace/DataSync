using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DataSync.LHYY.V2.Models.Enums;

namespace DataSync.LHYY.V2.Models.Entities;

/// <summary>
/// 字段映射规则
/// </summary>
[Table("esb_field_mapping", Schema = "lhyy")]
public class EsbFieldMapping
{
    public const string SubCardFilterTargetField = "__subcard_filter__";
    public const string DictMatchModeContains = "contains";
    public const string DictMatchModeContainsExcludeNegation = "contains_exclude_negation";
    public const string DictMatchModePriorityFirst = "priority_first";
    public const string DefaultDictMatchMode = DictMatchModeContains;

    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("tran_code")]
    [MaxLength(20)]
    public string TranCode { get; set; } = "";

    [Column("integration_project_code")]
    [MaxLength(50)]
    public string? IntegrationProjectCode { get; set; }

    [Column("sync_key")]
    [MaxLength(64)]
    public string? SyncKey { get; set; }

    [Column("last_sync_hash")]
    [MaxLength(64)]
    public string? LastSyncHash { get; set; }

    [Column("mapping_target")]
    public MappingTarget MappingTarget { get; set; }

    [Column("source_path")]
    [MaxLength(500)]
    public string SourcePath { get; set; } = "";

    [Column("target_field")]
    [MaxLength(200)]
    public string TargetField { get; set; } = "";

    [Column("card_id")]
    public Guid? CardId { get; set; }

    [Column("array_path")]
    [MaxLength(500)]
    public string? ArrayPath { get; set; }

    [Column("dict_code")]
    [MaxLength(100)]
    public string? DictCode { get; set; }

    [Column("dict_match_mode")]
    [MaxLength(50)]
    public string DictMatchMode { get; set; } = DefaultDictMatchMode;

    [Column("default_value")]
    [MaxLength(500)]
    public string? DefaultValue { get; set; }

    [Column("value_expression")]
    [MaxLength(500)]
    public string? ValueExpression { get; set; }

    [Column("is_required")]
    public bool IsRequired { get; set; }

    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("description")]
    [MaxLength(500)]
    public string? Description { get; set; }

    public static bool IsSubCardFilterMapping(EsbFieldMapping mapping) =>
        mapping.MappingTarget == MappingTarget.SubCard
        && string.Equals(mapping.TargetField, SubCardFilterTargetField, StringComparison.Ordinal);

    public static string NormalizeDictMatchMode(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            DictMatchModeContainsExcludeNegation => DictMatchModeContainsExcludeNegation,
            DictMatchModePriorityFirst => DictMatchModePriorityFirst,
            _ => DefaultDictMatchMode
        };
}
