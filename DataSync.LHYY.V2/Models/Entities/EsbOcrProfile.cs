using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DataSync.Common.Ocr;

namespace DataSync.LHYY.V2.Models.Entities;

/// <summary>
/// ESB OCR 转换配置。
/// </summary>
[Table("esb_ocr_profile", Schema = "lhyy")]
public class EsbOcrProfile
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

    [Column("source_kind")]
    public OcrSourceKind SourceKind { get; set; } = OcrSourceKind.FilePath;

    [Column("source_path")]
    [MaxLength(500)]
    public string SourcePath { get; set; } = "$.pdfPath";

    [Column("language")]
    [MaxLength(50)]
    public string Language { get; set; } = "chi_sim+eng";

    [Column("dpi")]
    public int Dpi { get; set; } = 300;

    [Column("page_seg_mode")]
    public int PageSegMode { get; set; } = 3;

    [Column("max_pages")]
    public int? MaxPages { get; set; }

    [Column("max_input_bytes")]
    public long? MaxInputBytes { get; set; }

    [Column("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 120;

    [Column("keep_work_files")]
    public bool KeepWorkFiles { get; set; }

    [Column("allowed_file_roots")]
    public string? AllowedFileRoots { get; set; }

    [Column("output_json_path")]
    [MaxLength(1000)]
    public string? OutputJsonPath { get; set; }

    [Column("extraction_rules", TypeName = "jsonb")]
    public string ExtractionRules { get; set; } = "[]";

    [Column("sample_message_id")]
    public long? SampleMessageId { get; set; }

    [Column("description")]
    [MaxLength(500)]
    public string? Description { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
