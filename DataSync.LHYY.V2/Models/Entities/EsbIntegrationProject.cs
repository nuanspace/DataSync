using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.LHYY.V2.Models.Entities;

/// <summary>
/// 接入项目
/// </summary>
[Table("esb_integration_project", Schema = "lhyy")]
public class EsbIntegrationProject
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("project_code")]
    [MaxLength(50)]
    public string ProjectCode { get; set; } = "";

    [Column("project_name")]
    [MaxLength(100)]
    public string ProjectName { get; set; } = "";

    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    [Column("description")]
    [MaxLength(500)]
    public string? Description { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
