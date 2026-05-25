using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.LHYY.V2.Models.Entities;

/// <summary>
/// 幂等键组成规则
/// </summary>
[Table("esb_idempotent_key_part", Schema = "lhyy")]
public class EsbIdempotentKeyPart
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

    [Column("source_path")]
    [MaxLength(500)]
    public string? SourcePath { get; set; }

    [Column("literal_value")]
    [MaxLength(500)]
    public string? LiteralValue { get; set; }

    [Column("default_value")]
    [MaxLength(500)]
    public string? DefaultValue { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    [Column("description")]
    [MaxLength(500)]
    public string? Description { get; set; }
}
