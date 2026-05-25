using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.LHYY.V2.Models.Entities;

/// <summary>
/// 字典配置
/// </summary>
[Table("esb_dict", Schema = "lhyy")]
public class EsbDict
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("dict_code")]
    [MaxLength(100)]
    public string DictCode { get; set; } = "";

    [Column("integration_project_code")]
    [MaxLength(50)]
    public string? IntegrationProjectCode { get; set; }

    [Column("source_value")]
    [MaxLength(200)]
    public string SourceValue { get; set; } = "";

    [Column("target_value")]
    [MaxLength(200)]
    public string TargetValue { get; set; } = "";

    [Column("sort_order")]
    public int SortOrder { get; set; }
}
