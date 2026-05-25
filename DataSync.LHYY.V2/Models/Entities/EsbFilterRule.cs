using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DataSync.LHYY.V2.Models.Enums;

namespace DataSync.LHYY.V2.Models.Entities;

/// <summary>
/// 统一过滤规则（接口级 + 映射级）
/// </summary>
[Table("esb_filter_rule", Schema = "lhyy")]
public class EsbFilterRule
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

    /// <summary>
    /// 源路径，支持 [] 数组遍历语法，如 Body.DiagList[].DiagCode
    /// </summary>
    [Column("source_path")]
    [MaxLength(500)]
    public string SourcePath { get; set; } = "";

    [Column("operator")]
    [MaxLength(20)]
    public string Operator { get; set; } = "eq";

    [Column("compare_value")]
    [MaxLength(500)]
    public string CompareValue { get; set; } = "";

    /// <summary>
    /// 规则组号：同组 AND，组间 OR
    /// </summary>
    [Column("rule_group")]
    public int RuleGroup { get; set; } = 1;

    /// <summary>
    /// 关联映射 ID（null = 接口级，非 null = 映射级）
    /// </summary>
    [Column("mapping_id")]
    public int? MappingId { get; set; }

    /// <summary>
    /// 作用范围（仅路径含 [] 时生效）
    /// </summary>
    [Column("filter_scope")]
    public FilterScope FilterScope { get; set; } = FilterScope.MessageCheck;

    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("description")]
    [MaxLength(500)]
    public string? Description { get; set; }
}
