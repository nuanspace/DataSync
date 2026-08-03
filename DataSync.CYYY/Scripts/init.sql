-- DataWeaver.DataSync 建表脚本
-- 数据库: dataweaver | Schema: cyyy

CREATE SCHEMA IF NOT EXISTS cyyy;

-- 1. 同步任务配置
CREATE TABLE cyyy.sync_tasks (
    id                      SERIAL PRIMARY KEY,
    name                    VARCHAR(100)  NOT NULL,                -- 任务名称
    code                    VARCHAR(50)   NOT NULL,                -- 唯一标识
    trigger_server_code     VARCHAR(100)  NOT NULL,                -- 触发源接口 serverCode
    trigger_time_field      VARCHAR(100)  NOT NULL,                -- 触发源时间字段名
    push_type               VARCHAR(20)   NOT NULL,                -- 推送方式: Api / Database
    push_target             VARCHAR(500)  NOT NULL,                -- 推送目标（API 地址或数据库连接串）
    patient_id_field        VARCHAR(100)  NOT NULL DEFAULT 'HIS_PAT_ID',  -- 患者ID字段名
    visit_sn_field          VARCHAR(100),                          -- 就诊号字段名
    polling_interval_seconds INT          NOT NULL DEFAULT 300,    -- 轮询间隔（秒）
    patient_concurrency     INT           NOT NULL DEFAULT 5,      -- 患者并发数 N
    api_concurrency         INT           NOT NULL DEFAULT 3,      -- 接口并发数 M
    enabled                 BOOLEAN       NOT NULL DEFAULT TRUE,

    CONSTRAINT uq_sync_tasks_code UNIQUE (code)
);

COMMENT ON TABLE  cyyy.sync_tasks IS '同步任务配置';
COMMENT ON COLUMN cyyy.sync_tasks.push_type IS 'Api = 推送到 HTTP 接口; Database = 写入数据库';

-- 2. 任务关联接口配置
CREATE TABLE cyyy.sync_task_interfaces (
    id              SERIAL PRIMARY KEY,
    task_id         INT           NOT NULL REFERENCES cyyy.sync_tasks(id) ON DELETE CASCADE,
    server_code     VARCHAR(100)  NOT NULL,                -- 数据湖接口 serverCode
    display_name    VARCHAR(100)  NOT NULL,                -- 显示名称
    query_field     VARCHAR(100)  NOT NULL,                -- 查询条件字段名
    interface_key   VARCHAR(50)   NOT NULL DEFAULT '',     -- 任务内接口唯一键
    parent_interface_key VARCHAR(50),                      -- 父接口唯一键
    is_required     BOOLEAN       NOT NULL DEFAULT FALSE,  -- 是否必要接口
    sort_order      INT           NOT NULL DEFAULT 1,      -- 执行顺序（同值并行）
    enabled         BOOLEAN       NOT NULL DEFAULT TRUE,
    query_value_field VARCHAR(100),                        -- 从触发记录取值字段
    parent_result_field VARCHAR(100),                      -- 从父接口结果取值字段
    mount_field     VARCHAR(100),                          -- 子接口挂载字段名
    route_field     VARCHAR(100),                          -- 父结果分流字段
    route_operator  VARCHAR(20)   NOT NULL DEFAULT 'eq',   -- 分流操作符
    route_value     TEXT,                                  -- 分流值
    output_fields   TEXT,                                  -- 根接口输出字段，逗号分隔
    push_params     TEXT,                                  -- 推送目标占位符参数
    inject_fields   TEXT,                                  -- 注入字段 JSON
    filter_conditions TEXT,                                -- 过滤条件 JSON
    query_path      TEXT,                                  -- 动态接口查询路径
    use_today_time_range BOOLEAN NOT NULL DEFAULT FALSE    -- 是否传入当天完整时间范围
);

CREATE UNIQUE INDEX ix_sync_task_interfaces_task_interface_key
    ON cyyy.sync_task_interfaces (task_id, interface_key);

COMMENT ON TABLE  cyyy.sync_task_interfaces IS '任务关联接口配置';
COMMENT ON COLUMN cyyy.sync_task_interfaces.is_required IS 'true = 必要接口，失败则跳过该患者';
COMMENT ON COLUMN cyyy.sync_task_interfaces.sort_order  IS '同 sort_order 的接口并行执行，不同值按序执行';

-- 3. 推送日志
CREATE TABLE cyyy.sync_logs (
    id              BIGSERIAL PRIMARY KEY,
    created_at      TIMESTAMP     NOT NULL DEFAULT NOW(),
    task_code       VARCHAR(50)   NOT NULL,                -- 任务标识
    his_pat_id      VARCHAR(50)   NOT NULL,                -- 患者业务ID
    pat_visit_sn    VARCHAR(50),                           -- 就诊号
    pat_name        VARCHAR(100),                          -- 患者姓名
    success         BOOLEAN       NOT NULL,
    error_message   TEXT,
    trigger_type    VARCHAR(20)   NOT NULL DEFAULT 'Scheduled'  -- Scheduled / Backfill
);

CREATE INDEX ix_sync_logs_created_at       ON cyyy.sync_logs (created_at);
CREATE INDEX ix_sync_logs_patient_task     ON cyyy.sync_logs (his_pat_id, task_code);

COMMENT ON TABLE cyyy.sync_logs IS '推送日志';

-- 4. 增量时间戳
CREATE TABLE cyyy.sync_checkpoints (
    task_code           VARCHAR(50) PRIMARY KEY,
    last_success_time   TIMESTAMP   NOT NULL,
    updated_at          TIMESTAMP   NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE cyyy.sync_checkpoints IS '增量同步检查点';

-- 4.1 采集日志
CREATE TABLE cyyy.ingestion_logs (
    id               BIGSERIAL PRIMARY KEY,
    created_at       TIMESTAMP    NOT NULL DEFAULT NOW(),
    server_code      VARCHAR(100) NOT NULL,
    source_name      VARCHAR(100) NOT NULL,
    trigger_type     VARCHAR(20)  NOT NULL DEFAULT 'Scheduled',
    time_field       VARCHAR(100),
    from_time        TIMESTAMP,
    to_time          TIMESTAMP,
    query_conditions TEXT         NOT NULL DEFAULT '[]',
    api_count        INT          NOT NULL DEFAULT 0,
    local_count      INT          NOT NULL DEFAULT 0,
    success          BOOLEAN      NOT NULL,
    error_message    TEXT,
    duration_ms      BIGINT       NOT NULL DEFAULT 0
);

CREATE INDEX ix_ingestion_logs_created_at ON cyyy.ingestion_logs (created_at);
CREATE INDEX ix_ingestion_logs_server_created ON cyyy.ingestion_logs (server_code, created_at);
CREATE INDEX ix_ingestion_logs_server_success ON cyyy.ingestion_logs (server_code, success);

COMMENT ON TABLE cyyy.ingestion_logs IS '采集日志';

-- 5. OAP-028 手术申请信息（采集落库）
CREATE TABLE cyyy.dl_oap_028 (
    "ROWKEY"          TEXT PRIMARY KEY,
    "PAT_VISIT_SN"    TEXT,
    "HIS_PAT_ID"      TEXT,
    "SC_APPLY_DEPT"   TEXT,      -- 申请科室编码（过滤字段）
    "ST_APPLY_DEPT"   TEXT,      -- 申请科室名称
    "SC_OPERATION"    TEXT,      -- 手术名称编码（过滤字段）
    "ST_OPERATION"    TEXT,      -- 手术名称
    "OPERATION_NAME"  TEXT,      -- 手术操作
    "APPLY_TIME"      TEXT,      -- 申请时间
    "SD_CANCEL"       TEXT,      -- 是否作废
    "CANCEL_FLAG"     TEXT,
    ingested_at       TIMESTAMP NOT NULL DEFAULT NOW()
);
CREATE INDEX ix_dl_oap_028_visit ON cyyy.dl_oap_028 ("PAT_VISIT_SN");
CREATE INDEX ix_dl_oap_028_dept_oper ON cyyy.dl_oap_028 ("SC_APPLY_DEPT", "SC_OPERATION");

COMMENT ON TABLE cyyy.dl_oap_028 IS 'OAP-028 手术申请信息（本地采集）';

-- 6. IHR-003 住院就诊信息（采集落库）
CREATE TABLE cyyy.dl_ihr_003 (
    "IHR_ROWKEY"      TEXT PRIMARY KEY,
    "PAT_VISIT_SN"    TEXT,
    "HIS_PAT_ID"      TEXT,
    "DISCHARGE_TIME"  TEXT,      -- 出院时间（过滤字段）
    "ADMISSION_TIME"  TEXT,      -- 入院时间
    "SC_ADM_DEPT"     TEXT,      -- 入院科室编码
    "ST_ADM_DEPT"     TEXT,      -- 入院科室名称
    "CASE_NO"         TEXT,      -- 病案号
    "INP_NO"          TEXT,      -- 住院号
    ingested_at       TIMESTAMP NOT NULL DEFAULT NOW()
);
CREATE INDEX ix_dl_ihr_003_visit ON cyyy.dl_ihr_003 ("PAT_VISIT_SN");
CREATE INDEX ix_dl_ihr_003_discharge ON cyyy.dl_ihr_003 ("DISCHARGE_TIME");

COMMENT ON TABLE cyyy.dl_ihr_003 IS 'IHR-003 住院就诊信息（本地采集）';

-- 7. 采集源配置
CREATE TABLE cyyy.ingestion_sources (
    id                       SERIAL PRIMARY KEY,
    name                     VARCHAR(100) NOT NULL,
    server_code              VARCHAR(100) NOT NULL UNIQUE,
    query_path               TEXT,
    time_field               VARCHAR(100) NOT NULL,
    primary_keys             VARCHAR(500) NOT NULL,
    polling_interval_seconds INT NOT NULL DEFAULT 300,
    enabled                  BOOLEAN NOT NULL DEFAULT TRUE,
    start_offset_minutes     INT NOT NULL DEFAULT 120,
    start_offset_unit        VARCHAR(20) NOT NULL DEFAULT 'hours',
    end_offset_minutes       INT NOT NULL DEFAULT 0,
    end_offset_unit          VARCHAR(20) NOT NULL DEFAULT 'hours'
);

COMMENT ON TABLE cyyy.ingestion_sources IS '采集源配置';

INSERT INTO cyyy.ingestion_sources (name, server_code, time_field, primary_keys) VALUES
('手术申请信息', 'JHIDS-BAS-OAP-028', 'APPLY_TIME', 'ROWKEY'),
('住院就诊信息', 'JHIDS-BAS-IHR-003', 'ADMISSION_TIME', 'IHR_ROWKEY');

-- 8. sync_tasks 新增字段
ALTER TABLE cyyy.sync_tasks
    ADD COLUMN IF NOT EXISTS trigger_mode VARCHAR(20) NOT NULL DEFAULT 'DataLake',
    ADD COLUMN IF NOT EXISTS trigger_conditions TEXT;

COMMENT ON COLUMN cyyy.sync_tasks.trigger_mode IS 'DataLake = 数据湖直查; Local = 本地采集表查询';
COMMENT ON COLUMN cyyy.sync_tasks.trigger_conditions IS '过滤条件 JSON（Local 模式使用）';

-- 9. sync_logs 去重索引
CREATE INDEX IF NOT EXISTS ix_sync_logs_dedup
    ON cyyy.sync_logs (task_code, his_pat_id, pat_visit_sn)
    WHERE success = true;

-- ============================================================
-- 示例数据（可按需修改后执行）
-- ============================================================

-- 导管室同步任务
INSERT INTO cyyy.sync_tasks (name, code, trigger_server_code, trigger_time_field, push_type, push_target, patient_id_field, visit_sn_field, polling_interval_seconds, patient_concurrency, api_concurrency)
VALUES ('导管室同步', 'CathLab', 'JHIDS-BAS-IHR-003', 'ADMISSION_TIME', 'Api', 'https://cathlab.example.com/api/push', 'HIS_PAT_ID', 'PAT_VISIT_SN', 300, 5, 3);

-- NTCare 同步任务
INSERT INTO cyyy.sync_tasks (name, code, trigger_server_code, trigger_time_field, push_type, push_target, patient_id_field, visit_sn_field, polling_interval_seconds, patient_concurrency, api_concurrency)
VALUES ('NTCare同步', 'NTCare', 'JHIDS-BAS-IHR-003', 'ADMISSION_TIME', 'Database', 'Host=localhost;Database=ntcare;Username=postgres;Password=postgres', 'HIS_PAT_ID', 'PAT_VISIT_SN', 300, 5, 3);

-- 导管室关联接口
INSERT INTO cyyy.sync_task_interfaces (task_id, server_code, display_name, query_field, is_required, sort_order) VALUES
(1, 'JHIDS-BAS-PAT-001', '患者基本信息', 'HIS_PAT_ID',   TRUE,  1),
(1, 'JHIDS-BAS-IDR-011', '诊断记录',     'PAT_VISIT_SN', FALSE, 2),
(1, 'JHIDS-BAS-IPR-012', '处方记录',     'PAT_VISIT_SN', FALSE, 2);

-- NTCare 关联接口
INSERT INTO cyyy.sync_task_interfaces (task_id, server_code, display_name, query_field, is_required, sort_order) VALUES
(2, 'JHIDS-BAS-PAT-001', '患者基本信息', 'HIS_PAT_ID',   TRUE,  1),
(2, 'JHIDS-BAS-IOR-004', '医嘱记录',     'PAT_VISIT_SN', FALSE, 2),
(2, 'JHIDS-BAS-IER-005', '检验记录',     'PAT_VISIT_SN', FALSE, 2),
(2, 'JHIDS-BAS-EAP-008', '检查报告',     'PAT_VISIT_SN', FALSE, 2);

-- 手术患者同步任务（Local 触发模式）
INSERT INTO cyyy.sync_tasks (
    name, code, trigger_server_code, trigger_time_field,
    push_type, push_target, patient_id_field, visit_sn_field,
    polling_interval_seconds, patient_concurrency, api_concurrency,
    trigger_mode, trigger_conditions
) VALUES (
    '手术患者同步', 'Surgery', 'JHIDS-BAS-OAP-028', 'APPLY_TIME',
    'Database', 'Host=localhost;Database=ntcare;Username=postgres;Password=postgres',
    'HIS_PAT_ID', 'PAT_VISIT_SN', 300, 5, 3,
    'Local', '{"SC_APPLY_DEPT":["DEPT001"],"SC_OPERATION":["OPER001"]}'
);

-- 手术同步关联接口（全景数据从数据湖实时拉取）
INSERT INTO cyyy.sync_task_interfaces (task_id, server_code, display_name, query_field, is_required, sort_order) VALUES
((SELECT id FROM cyyy.sync_tasks WHERE code='Surgery'), 'JHIDS-BAS-PAT-001', '患者基本信息', 'HIS_PAT_ID',   TRUE,  1),
((SELECT id FROM cyyy.sync_tasks WHERE code='Surgery'), 'JHIDS-BAS-IDR-011', '诊断记录',     'PAT_VISIT_SN', FALSE, 2),
((SELECT id FROM cyyy.sync_tasks WHERE code='Surgery'), 'JHIDS-BAS-IPR-012', '处方记录',     'PAT_VISIT_SN', FALSE, 2),
((SELECT id FROM cyyy.sync_tasks WHERE code='Surgery'), 'JHIDS-BAS-IOR-004', '医嘱记录',     'PAT_VISIT_SN', FALSE, 2),
((SELECT id FROM cyyy.sync_tasks WHERE code='Surgery'), 'JHIDS-BAS-IER-005', '检验记录',     'PAT_VISIT_SN', FALSE, 2),
((SELECT id FROM cyyy.sync_tasks WHERE code='Surgery'), 'JHIDS-BAS-EAP-008', '检查报告',     'PAT_VISIT_SN', FALSE, 2);

-- 10. 数据湖连接配置
CREATE TABLE cyyy.data_lake_configs (
    "Id"             SERIAL PRIMARY KEY,
    "BaseUrl"        VARCHAR(500) NOT NULL,
    "TokenEndpoint"  VARCHAR(200) NOT NULL DEFAULT '/auth/oauth/token',
    "QueryEndpoint"  VARCHAR(200) NOT NULL DEFAULT '/api/jhids4s/common/server/dataQuery',
    "ClientId"       VARCHAR(200) NOT NULL DEFAULT '',
    "ClientSecret"   VARCHAR(500) NOT NULL DEFAULT '',
    "SysCode"        VARCHAR(100) NOT NULL DEFAULT 'client-app',
    "PageSize"       INT NOT NULL DEFAULT 100,
    "MaxResultSize"  INT NOT NULL DEFAULT 10000,
    "UpdatedAt"      TIMESTAMP NOT NULL DEFAULT NOW()
);
COMMENT ON TABLE cyyy.data_lake_configs IS '数据湖连接配置（全局唯一）';

-- 11. 动态接口平台配置
CREATE TABLE IF NOT EXISTS cyyy.dynamic_api_configs (
    "Id"                          SERIAL PRIMARY KEY,
    "BaseUrl"                     VARCHAR(500) NOT NULL,
    "TokenEndpoint"               VARCHAR(200) NOT NULL DEFAULT '/api/v1/auth/token',
    "QueryEndpointPrefix"         VARCHAR(300) NOT NULL DEFAULT '/api/dynamic/api/inpatient/query',
    "AppKey"                      VARCHAR(200) NOT NULL DEFAULT '',
    "AppSecret"                   VARCHAR(500) NOT NULL DEFAULT '',
    "PageSize"                    INT NOT NULL DEFAULT 100,
    "RequestIntervalMilliseconds" INT NOT NULL DEFAULT 200,
    "UpdatedAt"                   TIMESTAMP NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE cyyy.dynamic_api_configs IS '动态接口平台连接与认证配置';
