using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.LHYY.V2.Models.Entities;

/// <summary>
/// 接入项目配置
/// </summary>
[Table("esb_integration_project_config", Schema = "lhyy")]
public class EsbIntegrationProjectConfig
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("integration_project_code")]
    [MaxLength(50)]
    public string IntegrationProjectCode { get; set; } = "";

    [Column("config_key")]
    [MaxLength(100)]
    public string ConfigKey { get; set; } = "";

    [Column("config_value")]
    public string? ConfigValue { get; set; }

    [Column("config_type")]
    [MaxLength(50)]
    public string? ConfigType { get; set; }

    [Column("description")]
    [MaxLength(500)]
    public string? Description { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
