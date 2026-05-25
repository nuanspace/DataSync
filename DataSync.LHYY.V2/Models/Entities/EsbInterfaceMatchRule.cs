using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.LHYY.V2.Models.Entities;

/// <summary>
/// 接口识别规则
/// </summary>
[Table("esb_interface_match_rule", Schema = "lhyy")]
public class EsbInterfaceMatchRule
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

    [Column("match_group")]
    public int MatchGroup { get; set; } = 1;

    [Column("source_path")]
    [MaxLength(500)]
    public string SourcePath { get; set; } = "";

    [Column("operator")]
    [MaxLength(20)]
    public string Operator { get; set; } = "eq";

    [Column("compare_value")]
    [MaxLength(500)]
    public string CompareValue { get; set; } = "";

    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("description")]
    [MaxLength(500)]
    public string? Description { get; set; }
}
