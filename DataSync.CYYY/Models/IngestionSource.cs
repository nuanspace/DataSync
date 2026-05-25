using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.CYYY.Models;

/// <summary>
/// 采集源配置
/// </summary>
[Table("ingestion_sources", Schema = "cyyy")]
public class IngestionSource
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("server_code")]
    public string ServerCode { get; set; } = "";

    [Column("time_field")]
    public string TimeField { get; set; } = "";

    [Column("polling_interval_seconds")]
    public int PollingIntervalSeconds { get; set; } = 300;

    [Column("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 开始时间 = 当前时间 - 该值（分钟）
    /// </summary>
    [Column("start_offset_minutes")]
    public int StartOffsetMinutes { get; set; } = 120;

    /// <summary>
    /// 开始偏移量在编辑页选择的单位，偏移量数值仍按分钟存储。
    /// </summary>
    [Column("start_offset_unit")]
    public string StartOffsetUnit { get; set; } = "hours";

    /// <summary>
    /// 结束时间 = 当前时间 - 该值（分钟），0 表示当前时间
    /// </summary>
    [Column("end_offset_minutes")]
    public int EndOffsetMinutes { get; set; } = 0;

    /// <summary>
    /// 结束偏移量在编辑页选择的单位，偏移量数值仍按分钟存储。
    /// </summary>
    [Column("end_offset_unit")]
    public string EndOffsetUnit { get; set; } = "hours";

    /// <summary>
    /// 主键字段列表（逗号分隔），如 "ROWKEY" 或 "HIS_PAT_ID,PAT_VISIT_SN"
    /// </summary>
    [Column("primary_keys")]
    public string PrimaryKeys { get; set; } = "";

    /// <summary>
    /// 获取主键字段数组
    /// </summary>
    [NotMapped]
    public string[] PrimaryKeyArray => PrimaryKeys
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// 额外查询条件 JSON，格式: [{"column":"x","type":"eq","value":"y"}]
    /// </summary>
    [Column("conditions")]
    public string? Conditions { get; set; }
}
