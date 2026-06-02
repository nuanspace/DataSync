using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.CYYY.Models;

/// <summary>
/// 可复用数据库连接资源。
/// </summary>
[Table("database_resources", Schema = "cyyy")]
public class DatabaseResource
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("database_type")]
    public string DatabaseType { get; set; } = "SqlServer";

    [Column("host")]
    public string Host { get; set; } = "";

    [Column("database_name")]
    public string DatabaseName { get; set; } = "";

    [Column("username")]
    public string Username { get; set; } = "";

    [Column("password")]
    public string? Password { get; set; }

    [Column("trust_certificate")]
    public bool TrustCertificate { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
