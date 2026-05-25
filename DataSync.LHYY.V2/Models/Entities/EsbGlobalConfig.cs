using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.LHYY.V2.Models.Entities;

/// <summary>
/// 全局配置
/// </summary>
[Table("esb_global_config", Schema = "lhyy")]
public class EsbGlobalConfig
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

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
}
