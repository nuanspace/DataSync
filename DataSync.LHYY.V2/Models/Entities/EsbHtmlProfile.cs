using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.LHYY.V2.Models.Entities;

/// <summary>
/// ESB HTML 文本解析配置。
/// </summary>
[Table("esb_html_profile", Schema = "lhyy")]
public class EsbHtmlProfile
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

    [Column("profile_name")]
    [MaxLength(100)]
    public string? ProfileName { get; set; }

    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    [Column("source_path")]
    [MaxLength(500)]
    public string SourcePath { get; set; } = "$main.FILE_CONTENT";

    [Column("max_input_bytes")]
    public long MaxInputBytes { get; set; } = 5L * 1024 * 1024;

    [Column("preserve_sections")]
    public bool PreserveSections { get; set; } = true;

    [Column("section_headings")]
    public string SectionHeadings { get; set; } =
        "主诉\n现病史\n既往史\n个人史\n婚育史\n家族史\n体格检查\n专科检查\n辅助检查\n确定诊断\n初步诊断";

    [Column("extraction_rules", TypeName = "jsonb")]
    public string ExtractionRules { get; set; } = "[]";

    [Column("description")]
    [MaxLength(500)]
    public string? Description { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
