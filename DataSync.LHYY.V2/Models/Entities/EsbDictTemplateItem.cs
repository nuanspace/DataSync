using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.LHYY.V2.Models.Entities;

[Table("esb_dict_template_item", Schema = "lhyy")]
public class EsbDictTemplateItem
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("template_id")]
    public int TemplateId { get; set; }

    [Column("source_value")]
    [MaxLength(200)]
    public string SourceValue { get; set; } = "";

    [Column("target_value")]
    [MaxLength(200)]
    public string TargetValue { get; set; } = "";

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("description")]
    [MaxLength(500)]
    public string? Description { get; set; }

    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    public EsbDictTemplate? Template { get; set; }
}
