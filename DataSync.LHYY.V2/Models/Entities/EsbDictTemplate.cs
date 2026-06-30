using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.LHYY.V2.Models.Entities;

[Table("esb_dict_template", Schema = "lhyy")]
public class EsbDictTemplate
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("template_code")]
    [MaxLength(100)]
    public string TemplateCode { get; set; } = "";

    [Column("template_name")]
    [MaxLength(100)]
    public string TemplateName { get; set; } = "";

    [Column("category")]
    [MaxLength(50)]
    public string Category { get; set; } = "";

    [Column("default_dict_code")]
    [MaxLength(100)]
    public string? DefaultDictCode { get; set; }

    [Column("default_match_mode")]
    [MaxLength(50)]
    public string DefaultMatchMode { get; set; } = EsbFieldMapping.DefaultDictMatchMode;

    [Column("description")]
    [MaxLength(500)]
    public string? Description { get; set; }

    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public List<EsbDictTemplateItem> Items { get; set; } = [];
}
