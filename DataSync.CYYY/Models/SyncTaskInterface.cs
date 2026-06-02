using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.CYYY.Models;

/// <summary>
/// 任务关联接口配置
/// </summary>
[Table("sync_task_interfaces", Schema = "cyyy")]
public class SyncTaskInterface
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("task_id")]
    public int TaskId { get; set; }

    /// <summary>
    /// 数据湖接口 serverCode
    /// </summary>
    [Column("server_code")]
    public string ServerCode { get; set; } = "";

    [Column("display_name")]
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// 查询来源类型：DataLake / Database
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
    /// 数据库查询模板，支持 @queryValue，Oracle 也可写 :queryValue。
    /// </summary>
    [Column("query_sql")]
    public string? QuerySql { get; set; }

    /// <summary>
    /// 查询条件字段名，如 HIS_PAT_ID 或 PAT_VISIT_SN
    /// </summary>
    [Column("query_field")]
    public string QueryField { get; set; } = "";

    /// <summary>
    /// 是否必要接口（失败则整个患者跳过）
    /// </summary>
    [Column("is_required")]
    public bool IsRequired { get; set; }

    /// <summary>
    /// 执行顺序（同优先级的并行执行）
    /// </summary>
    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 任务内接口唯一键，用于配置父子关系。
    /// </summary>
    [Column("interface_key")]
    public string InterfaceKey { get; set; } = "";

    /// <summary>
    /// 父接口唯一键。为空表示独立接口或根接口。
    /// </summary>
    [Column("parent_interface_key")]
    public string? ParentInterfaceKey { get; set; }

    /// <summary>
    /// 查询值来源字段名 — 从触发记录中取哪个字段的值作为查询参数
    /// 为空时回退到旧逻辑（根据 QueryField 与 PatientIdField 的比较决定）
    /// </summary>
    [Column("query_value_field")]
    public string? QueryValueField { get; set; }

    /// <summary>
    /// 父接口结果取值字段。仅子接口使用。
    /// </summary>
    [Column("parent_result_field")]
    public string? ParentResultField { get; set; }

    /// <summary>
    /// 挂载到父结果中的字段名。仅子接口使用。
    /// </summary>
    [Column("mount_field")]
    public string? MountField { get; set; }

    /// <summary>
    /// 分流字段。仅在同一父接口下有多个候选子接口时使用。
    /// </summary>
    [Column("route_field")]
    public string? RouteField { get; set; }

    /// <summary>
    /// 分流操作符，当前支持 eq / in。
    /// </summary>
    [Column("route_operator")]
    public string? RouteOperator { get; set; }

    /// <summary>
    /// 分流匹配值。in 使用逗号分隔。
    /// </summary>
    [Column("route_value")]
    public string? RouteValue { get; set; }

    /// <summary>
    /// 根接口输出字段列表，逗号分隔。为空表示输出整条根记录。
    /// </summary>
    [Column("output_fields")]
    public string? OutputFields { get; set; }

    /// <summary>
    /// URL 路由参数（JSON 键值对），用于替换 PushTarget 中的 {xxx} 占位符
    /// 格式：{"messageType":"surgery_order","version":"v2"}
    /// </summary>
    [Column("push_params")]
    public string? PushParams { get; set; }

    /// <summary>
    /// 注入字段配置 — 从触发记录中注入到查询结果的字段列表（JSON 数组）
    /// 格式：["ADMISSION_TIME", "DISCHARGE_TIME"]
    /// 推送前会将触发记录中这些字段的值添加到每条查询结果记录中
    /// </summary>
    [Column("inject_fields")]
    public string? InjectFields { get; set; }

    /// <summary>
    /// 过滤条件 — 数据湖查询时的额外过滤条件（JSON 数组）
    /// 格式：[{"column":"STATUS","type":"eq","value":"1"}]
    /// </summary>
    [Column("filter_conditions")]
    public string? FilterConditions { get; set; }

    [ForeignKey(nameof(TaskId))]
    public SyncTask? Task { get; set; }

    [ForeignKey(nameof(DatabaseResourceId))]
    public DatabaseResource? DatabaseResource { get; set; }
}
