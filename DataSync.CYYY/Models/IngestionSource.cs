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

    /// <summary>
    /// 来源类型：DataLake / DynamicApi / Database
    /// </summary>
    [Column("source_type")]
    public string SourceType { get; set; } = "DataLake";

    [Column("database_resource_id")]
    public int? DatabaseResourceId { get; set; }

    /// <summary>
    /// 数据库类型：SqlServer / Oracle。
    /// </summary>
    [Column("database_type")]
    public string? DatabaseType { get; set; } = "SqlServer";

    /// <summary>
    /// 数据库连接串名称或完整连接串。
    /// </summary>
    [Column("connection_string_name")]
    public string? ConnectionStringName { get; set; }

    /// <summary>
    /// 数据库主机，可包含端口、实例名或 Oracle 服务名。
    /// </summary>
    [Column("sql_server_host")]
    public string? SqlServerHost { get; set; }

    /// <summary>
    /// 数据库名或 Oracle 服务名。
    /// </summary>
    [Column("sql_server_database")]
    public string? SqlServerDatabase { get; set; }

    /// <summary>
    /// 数据库用户名。
    /// </summary>
    [Column("sql_server_username")]
    public string? SqlServerUsername { get; set; }

    /// <summary>
    /// 数据库密码。
    /// </summary>
    [Column("sql_server_password")]
    public string? SqlServerPassword { get; set; }

    /// <summary>
    /// 是否信任 SQL Server 证书。
    /// </summary>
    [Column("sql_server_trust_certificate")]
    public bool SqlServerTrustCertificate { get; set; } = true;

    /// <summary>
    /// 数据库主查询模板，支持 @from/@to，Oracle 也可写 :from/:to。
    /// </summary>
    [Column("query_sql")]
    public string? QuerySql { get; set; }

    /// <summary>
    /// 动态接口采集查询路径，填写查询端点前缀后的相对路径。
    /// </summary>
    [Column("query_path")]
    public string? QueryPath { get; set; }

    [Column("time_field")]
    public string TimeField { get; set; } = "";

    /// <summary>
    /// 检查点回看分钟数，防止边界数据漏采。
    /// </summary>
    [Column("lookback_minutes")]
    public int LookbackMinutes { get; set; } = 5;

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

    [ForeignKey(nameof(DatabaseResourceId))]
    public DatabaseResource? DatabaseResource { get; set; }
}
