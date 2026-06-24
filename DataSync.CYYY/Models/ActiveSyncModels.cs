using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSync.CYYY.Models;

/// <summary>
/// Active 病历补采任务。
/// </summary>
[Table("active_sync_tasks", Schema = "cyyy")]
public class ActiveSyncTask
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("code")]
    public string Code { get; set; } = "";

    [Column("enabled")]
    public bool Enabled { get; set; } = true;

    [Column("sync_task_id")]
    public int? SyncTaskId { get; set; }

    [Column("active_records_url")]
    public string ActiveRecordsUrl { get; set; } = "";

    [Column("integration_project_code")]
    public string? IntegrationProjectCode { get; set; }

    [Column("push_type")]
    public string PushType { get; set; } = "Api";

    [Column("push_target")]
    public string PushTarget { get; set; } = "";

    [Column("case_batch_size")]
    public int CaseBatchSize { get; set; } = 50;

    [Column("concurrency")]
    public int Concurrency { get; set; } = 3;

    [Column("polling_interval_seconds")]
    public int PollingIntervalSeconds { get; set; } = 300;

    [Column("empty_backoff_base_seconds")]
    public int EmptyBackoffBaseSeconds { get; set; } = 1800;

    [Column("empty_backoff_max_seconds")]
    public int EmptyBackoffMaxSeconds { get; set; } = 7200;

    [Column("remark")]
    public string? Remark { get; set; }

    [ForeignKey(nameof(SyncTaskId))]
    public SyncTask? SyncTask { get; set; }

    public List<ActiveSyncSource> Sources { get; set; } = [];
}

/// <summary>
/// Active 补采任务下挂的数据源。
/// </summary>
[Table("active_sync_sources", Schema = "cyyy")]
public class ActiveSyncSource
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("task_id")]
    public int TaskId { get; set; }

    [Column("sync_task_interface_id")]
    public int? SyncTaskInterfaceId { get; set; }

    [Column("data_type")]
    public string DataType { get; set; } = "";

    [Column("server_code")]
    public string ServerCode { get; set; } = "";

    [Column("display_name")]
    public string DisplayName { get; set; } = "";

    [Column("database_resource_id")]
    public int? DatabaseResourceId { get; set; }

    [Column("query_sql")]
    public string QuerySql { get; set; } = "";

    [Column("inpatient_no_parameter")]
    public string InpatientNoParameter { get; set; } = "inpatientNo";

    [Column("admission_time_parameter")]
    public string? AdmissionTimeParameter { get; set; } = "admissionTime";

    [Column("source_record_key_fields")]
    public string SourceRecordKeyFields { get; set; } = "";

    [Column("polling_interval_seconds")]
    public int PollingIntervalSeconds { get; set; } = 1800;

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("enabled")]
    public bool Enabled { get; set; } = true;

    [ForeignKey(nameof(TaskId))]
    public ActiveSyncTask? Task { get; set; }

    [ForeignKey(nameof(SyncTaskInterfaceId))]
    public SyncTaskInterface? SyncTaskInterface { get; set; }

    [ForeignKey(nameof(DatabaseResourceId))]
    public DatabaseResource? DatabaseResource { get; set; }

    [NotMapped]
    public string[] SourceRecordKeyArray => SourceRecordKeyFields
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// 单个 Active 病历在单个数据源上的轮询状态。
/// </summary>
[Table("active_sync_case_source_states", Schema = "cyyy")]
public class ActiveSyncCaseSourceState
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("task_id")]
    public int TaskId { get; set; }

    [Column("source_id")]
    public int SourceId { get; set; }

    [Column("inpatient_no")]
    public string InpatientNo { get; set; } = "";

    [Column("empty_count")]
    public int EmptyCount { get; set; }

    [Column("next_query_time")]
    public DateTime? NextQueryTime { get; set; }

    [Column("last_query_at")]
    public DateTime? LastQueryAt { get; set; }

    [Column("last_result_count")]
    public int LastResultCount { get; set; }

    [Column("last_error")]
    public string? LastError { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Active 补采记录级去重回执。
/// </summary>
[Table("active_sync_record_receipts", Schema = "cyyy")]
public class ActiveSyncRecordReceipt
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("task_id")]
    public int TaskId { get; set; }

    [Column("source_id")]
    public int SourceId { get; set; }

    [Column("inpatient_no")]
    public string InpatientNo { get; set; } = "";

    [Column("source_record_key")]
    public string SourceRecordKey { get; set; } = "";

    [Column("pushed_at")]
    public DateTime PushedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Active 补采运行日志。
/// </summary>
[Table("active_sync_run_logs", Schema = "cyyy")]
public class ActiveSyncRunLog
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("task_id")]
    public int TaskId { get; set; }

    [Column("source_id")]
    public int? SourceId { get; set; }

    [Column("task_name")]
    public string TaskName { get; set; } = "";

    [Column("source_name")]
    public string? SourceName { get; set; }

    [Column("inpatient_no")]
    public string? InpatientNo { get; set; }

    [Column("level")]
    public string Level { get; set; } = "Info";

    [Column("message")]
    public string Message { get; set; } = "";

    [Column("active_case_count")]
    public int ActiveCaseCount { get; set; }

    [Column("query_count")]
    public int QueryCount { get; set; }

    [Column("pushed_count")]
    public int PushedCount { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public class ActiveMedicalRecordInfo
{
    public long Cursor { get; set; }

    public string Mrn { get; set; } = "";

    public string InpatientNo { get; set; } = "";

    public string? VisitNo { get; set; }

    public DateTime? AdmissionTime { get; set; }

    public Guid? PatientId { get; set; }

    public Guid? EventId { get; set; }
}
