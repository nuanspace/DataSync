using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.CYYY.Models;

/// <summary>
/// 数据湖接口字典
/// </summary>
[Table("data_lake_interfaces", Schema = "cyyy")]
public class DataLakeInterface
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("server_code")]
    public string ServerCode { get; set; } = "";

    [Column("name")]
    public string Name { get; set; } = "";
}
