using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.LHYY.V2.Models.Entities;

/// <summary>
/// 接入项目文档
/// </summary>
[Table("esb_integration_project_document", Schema = "lhyy")]
public class EsbIntegrationProjectDocument
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("integration_project_code")]
    [MaxLength(50)]
    public string IntegrationProjectCode { get; set; } = "";

    [Column("category")]
    [MaxLength(50)]
    public string Category { get; set; } = "";

    [Column("title")]
    [MaxLength(200)]
    public string Title { get; set; } = "";

    [Column("original_file_name")]
    [MaxLength(255)]
    public string OriginalFileName { get; set; } = "";

    [Column("stored_file_name")]
    [MaxLength(255)]
    public string StoredFileName { get; set; } = "";

    [Column("stored_relative_path")]
    [MaxLength(500)]
    public string StoredRelativePath { get; set; } = "";

    [Column("content_type")]
    [MaxLength(200)]
    public string? ContentType { get; set; }

    [Column("file_size")]
    public long FileSize { get; set; }

    [Column("remark")]
    [MaxLength(1000)]
    public string? Remark { get; set; }

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
