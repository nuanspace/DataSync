-- 手术患者同步功能迁移脚本

-- 1. OAP-028 手术申请信息（采集落库）
CREATE TABLE IF NOT EXISTS cyyy.dl_oap_028 (
    "ROWKEY"          TEXT PRIMARY KEY,
    "PAT_VISIT_SN"    TEXT,
    "HIS_PAT_ID"      TEXT,
    "SC_APPLY_DEPT"   TEXT,
    "ST_APPLY_DEPT"   TEXT,
    "SC_OPERATION"    TEXT,
    "ST_OPERATION"    TEXT,
    "OPERATION_NAME"  TEXT,
    "APPLY_TIME"      TEXT,
    "SD_CANCEL"       TEXT,
    "CANCEL_FLAG"     TEXT,
    ingested_at       TIMESTAMP NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_dl_oap_028_visit ON cyyy.dl_oap_028 ("PAT_VISIT_SN");
CREATE INDEX IF NOT EXISTS ix_dl_oap_028_dept_oper ON cyyy.dl_oap_028 ("SC_APPLY_DEPT", "SC_OPERATION");

-- 2. IHR-003 住院就诊信息（采集落库）
CREATE TABLE IF NOT EXISTS cyyy.dl_ihr_003 (
    "ROWKEY"          TEXT PRIMARY KEY,
    "PAT_VISIT_SN"    TEXT,
    "HIS_PAT_ID"      TEXT,
    "DISCHARGE_TIME"  TEXT,
    "ADMISSION_TIME"  TEXT,
    "SC_ADM_DEPT"     TEXT,
    "ST_ADM_DEPT"     TEXT,
    "CASE_NO"         TEXT,
    "INP_NO"          TEXT,
    ingested_at       TIMESTAMP NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_dl_ihr_003_visit ON cyyy.dl_ihr_003 ("PAT_VISIT_SN");
CREATE INDEX IF NOT EXISTS ix_dl_ihr_003_discharge ON cyyy.dl_ihr_003 ("DISCHARGE_TIME");

-- 3. 采集源配置
CREATE TABLE IF NOT EXISTS cyyy.ingestion_sources (
    id                       SERIAL PRIMARY KEY,
    name                     VARCHAR(100) NOT NULL,
    server_code              VARCHAR(100) NOT NULL UNIQUE,
    time_field               VARCHAR(100) NOT NULL,
    polling_interval_seconds INT NOT NULL DEFAULT 300,
    enabled                  BOOLEAN NOT NULL DEFAULT TRUE
);

INSERT INTO cyyy.ingestion_sources (name, server_code, time_field)
SELECT '手术申请信息', 'JHIDS-BAS-OAP-028', 'APPLY_TIME'
WHERE NOT EXISTS (SELECT 1 FROM cyyy.ingestion_sources WHERE server_code = 'JHIDS-BAS-OAP-028');

INSERT INTO cyyy.ingestion_sources (name, server_code, time_field)
SELECT '住院就诊信息', 'JHIDS-BAS-IHR-003', 'ADMISSION_TIME'
WHERE NOT EXISTS (SELECT 1 FROM cyyy.ingestion_sources WHERE server_code = 'JHIDS-BAS-IHR-003');

-- 4. sync_tasks 新增字段
ALTER TABLE cyyy.sync_tasks
    ADD COLUMN IF NOT EXISTS trigger_mode VARCHAR(20) NOT NULL DEFAULT 'DataLake',
    ADD COLUMN IF NOT EXISTS trigger_conditions TEXT;

-- 5. sync_logs 去重索引
CREATE INDEX IF NOT EXISTS ix_sync_logs_dedup
    ON cyyy.sync_logs (task_code, his_pat_id, pat_visit_sn)
    WHERE success = true;

-- 6. 手术患者同步任务
INSERT INTO cyyy.sync_tasks (
    name, code, trigger_server_code, trigger_time_field,
    push_type, push_target, patient_id_field, visit_sn_field,
    polling_interval_seconds, patient_concurrency, api_concurrency,
    enabled, trigger_mode, trigger_conditions
)
SELECT
    '手术患者同步', 'Surgery', 'JHIDS-BAS-OAP-028', 'APPLY_TIME',
    'Database', 'Host=localhost;Database=ntcare;Username=postgres;Password=postgres',
    'HIS_PAT_ID', 'PAT_VISIT_SN', 300, 5, 3,
    TRUE, 'Local', '{"SC_APPLY_DEPT":["DEPT001"],"SC_OPERATION":["OPER001"]}'
WHERE NOT EXISTS (SELECT 1 FROM cyyy.sync_tasks WHERE code = 'Surgery');

-- 7. 手术同步关联接口
INSERT INTO cyyy.sync_task_interfaces (task_id, server_code, display_name, query_field, is_required, sort_order, enabled)
SELECT id, 'JHIDS-BAS-PAT-001', '患者基本信息', 'HIS_PAT_ID', TRUE, 1, TRUE
FROM cyyy.sync_tasks WHERE code = 'Surgery'
AND NOT EXISTS (
    SELECT 1 FROM cyyy.sync_task_interfaces ti
    JOIN cyyy.sync_tasks t ON ti.task_id = t.id
    WHERE t.code = 'Surgery' AND ti.server_code = 'JHIDS-BAS-PAT-001'
);

INSERT INTO cyyy.sync_task_interfaces (task_id, server_code, display_name, query_field, is_required, sort_order, enabled)
SELECT id, 'JHIDS-BAS-IDR-011', '诊断记录', 'PAT_VISIT_SN', FALSE, 2, TRUE
FROM cyyy.sync_tasks WHERE code = 'Surgery'
AND NOT EXISTS (
    SELECT 1 FROM cyyy.sync_task_interfaces ti
    JOIN cyyy.sync_tasks t ON ti.task_id = t.id
    WHERE t.code = 'Surgery' AND ti.server_code = 'JHIDS-BAS-IDR-011'
);

INSERT INTO cyyy.sync_task_interfaces (task_id, server_code, display_name, query_field, is_required, sort_order, enabled)
SELECT id, 'JHIDS-BAS-IPR-012', '处方记录', 'PAT_VISIT_SN', FALSE, 2, TRUE
FROM cyyy.sync_tasks WHERE code = 'Surgery'
AND NOT EXISTS (
    SELECT 1 FROM cyyy.sync_task_interfaces ti
    JOIN cyyy.sync_tasks t ON ti.task_id = t.id
    WHERE t.code = 'Surgery' AND ti.server_code = 'JHIDS-BAS-IPR-012'
);

INSERT INTO cyyy.sync_task_interfaces (task_id, server_code, display_name, query_field, is_required, sort_order, enabled)
SELECT id, 'JHIDS-BAS-IOR-004', '医嘱记录', 'PAT_VISIT_SN', FALSE, 2, TRUE
FROM cyyy.sync_tasks WHERE code = 'Surgery'
AND NOT EXISTS (
    SELECT 1 FROM cyyy.sync_task_interfaces ti
    JOIN cyyy.sync_tasks t ON ti.task_id = t.id
    WHERE t.code = 'Surgery' AND ti.server_code = 'JHIDS-BAS-IOR-004'
);

INSERT INTO cyyy.sync_task_interfaces (task_id, server_code, display_name, query_field, is_required, sort_order, enabled)
SELECT id, 'JHIDS-BAS-IER-005', '检验记录', 'PAT_VISIT_SN', FALSE, 2, TRUE
FROM cyyy.sync_tasks WHERE code = 'Surgery'
AND NOT EXISTS (
    SELECT 1 FROM cyyy.sync_task_interfaces ti
    JOIN cyyy.sync_tasks t ON ti.task_id = t.id
    WHERE t.code = 'Surgery' AND ti.server_code = 'JHIDS-BAS-IER-005'
);

INSERT INTO cyyy.sync_task_interfaces (task_id, server_code, display_name, query_field, is_required, sort_order, enabled)
SELECT id, 'JHIDS-BAS-EAP-008', '检查报告', 'PAT_VISIT_SN', FALSE, 2, TRUE
FROM cyyy.sync_tasks WHERE code = 'Surgery'
AND NOT EXISTS (
    SELECT 1 FROM cyyy.sync_task_interfaces ti
    JOIN cyyy.sync_tasks t ON ti.task_id = t.id
    WHERE t.code = 'Surgery' AND ti.server_code = 'JHIDS-BAS-EAP-008'
);
