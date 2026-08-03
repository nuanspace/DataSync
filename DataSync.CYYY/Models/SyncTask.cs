using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.CYYY.Models;

/// <summary>
/// 同步任务配置
/// </summary>
[Table("sync_tasks", Schema = "cyyy")]
public class SyncTask
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("code")]
    public string Code { get; set; } = "";

    /// <summary>
    /// 触发源接口 serverCode
    /// </summary>
    [Column("trigger_server_code")]
    public string TriggerServerCode { get; set; } = "";

    /// <summary>
    /// 附加触发采集源编码，逗号分隔
    /// </summary>
    [Column("additional_trigger_server_codes")]
    public string AdditionalTriggerServerCodes { get; set; } = "";

    [NotMapped]
    public string[] AdditionalTriggerServerCodeArray => AdditionalTriggerServerCodes
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// 推送方式：Api / Database
    /// </summary>
    [Column("push_type")]
    public string PushType { get; set; } = "";

    /// <summary>
    /// 推送目标（API地址或数据库连接串）
    /// </summary>
    [Column("push_target")]
    public string PushTarget { get; set; } = "";

    /// <summary>
    /// 是否启用触发记录单独推送
    /// </summary>
    [Column("enable_trigger_record_push")]
    public bool EnableTriggerRecordPush { get; set; }

    /// <summary>
    /// 触发记录单独推送目标
    /// </summary>
    [Column("trigger_push_target")]
    public string? TriggerPushTarget { get; set; }

    /// <summary>
    /// 触发记录单独推送路由参数（JSON 键值对）
    /// </summary>
    [Column("trigger_push_params")]
    public string? TriggerPushParams { get; set; }

    /// <summary>
    /// 触发源中患者ID字段名
    /// </summary>
    [Column("patient_id_field")]
    public string PatientIdField { get; set; } = "HIS_PAT_ID";

    /// <summary>
    /// 触发源中就诊号字段名
    /// </summary>
    [Column("visit_sn_field")]
    public string? VisitSnField { get; set; }

    [Column("polling_interval_seconds")]
    public int PollingIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// 患者并发数 N
    /// </summary>
    [Column("patient_concurrency")]
    public int PatientConcurrency { get; set; } = 5;

    /// <summary>
    /// 接口并发数 M
    /// </summary>
    [Column("api_concurrency")]
    public int ApiConcurrency { get; set; } = 3;

    [Column("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 过滤条件 JSON
    /// 新格式: {"logic":"AND","excludeSynced":true,"rules":[{"interface":"...","field":"...","mode":"include","values":["..."]}]}
    /// 兼容旧格式: {"SC_APPLY_DEPT":["1001"],"SC_OPERATION":["0205"]}
    /// </summary>
    [Column("trigger_conditions")]
    public string? TriggerConditions { get; set; }

    public List<SyncTaskInterface> Interfaces { get; set; } = [];
}
