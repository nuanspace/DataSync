ALTER TABLE cyyy.sync_tasks
    ADD COLUMN IF NOT EXISTS patient_continuous_sync_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS patient_continuous_sync_interval_seconds INT NOT NULL DEFAULT 1800,
    ADD COLUMN IF NOT EXISTS patient_continuous_sync_lookback_minutes INT NOT NULL DEFAULT 5,
    ADD COLUMN IF NOT EXISTS admission_source_server_code TEXT,
    ADD COLUMN IF NOT EXISTS admission_time_field TEXT,
    ADD COLUMN IF NOT EXISTS discharge_source_server_code TEXT,
    ADD COLUMN IF NOT EXISTS discharge_time_field TEXT;

ALTER TABLE cyyy.sync_task_interfaces
    ADD COLUMN IF NOT EXISTS patient_continuous_sync_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS continuous_record_key_fields TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS continuous_use_row_hash BOOLEAN NOT NULL DEFAULT FALSE;

CREATE TABLE IF NOT EXISTS cyyy.patient_continuous_sync_sessions
(
    id BIGSERIAL PRIMARY KEY,
    task_id INT NOT NULL,
    patient_id TEXT NOT NULL,
    visit_sn TEXT NOT NULL,
    admission_time TIMESTAMP,
    discharge_time TIMESTAMP,
    trigger_record_json TEXT NOT NULL DEFAULT '{}',
    status TEXT NOT NULL DEFAULT 'WaitingData',
    next_run_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_error TEXT,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_patient_continuous_sync_sessions_task_patient_visit
    ON cyyy.patient_continuous_sync_sessions(task_id, patient_id, visit_sn);
CREATE INDEX IF NOT EXISTS ix_patient_continuous_sync_sessions_due
    ON cyyy.patient_continuous_sync_sessions(task_id, status, next_run_at);

CREATE TABLE IF NOT EXISTS cyyy.patient_continuous_sync_interface_states
(
    id BIGSERIAL PRIMARY KEY,
    session_id BIGINT NOT NULL,
    interface_id INT NOT NULL,
    watermark TIMESTAMP,
    next_run_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    status TEXT NOT NULL DEFAULT 'Pending',
    retry_count INT NOT NULL DEFAULT 0,
    last_error TEXT,
    last_started_at TIMESTAMP,
    last_success_at TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_patient_continuous_sync_interface_states_session_interface
    ON cyyy.patient_continuous_sync_interface_states(session_id, interface_id);
CREATE INDEX IF NOT EXISTS ix_patient_continuous_sync_interface_states_due
    ON cyyy.patient_continuous_sync_interface_states(status, next_run_at);

CREATE TABLE IF NOT EXISTS cyyy.patient_continuous_sync_receipts
(
    id BIGSERIAL PRIMARY KEY,
    session_id BIGINT NOT NULL,
    interface_id INT NOT NULL,
    record_key TEXT NOT NULL,
    pushed_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_patient_continuous_sync_receipts_session_interface_key
    ON cyyy.patient_continuous_sync_receipts(session_id, interface_id, record_key);
CREATE INDEX IF NOT EXISTS ix_patient_continuous_sync_receipts_pushed_at
    ON cyyy.patient_continuous_sync_receipts(pushed_at);

COMMENT ON COLUMN cyyy.sync_tasks.patient_continuous_sync_enabled IS '是否启用患者持续增量同步';
COMMENT ON COLUMN cyyy.sync_tasks.patient_continuous_sync_interval_seconds IS '患者持续同步间隔秒数';
COMMENT ON COLUMN cyyy.sync_tasks.patient_continuous_sync_lookback_minutes IS '增量查询水位回看分钟数';
COMMENT ON COLUMN cyyy.sync_task_interfaces.continuous_record_key_fields IS '持续同步记录唯一键字段，逗号分隔';
COMMENT ON COLUMN cyyy.sync_task_interfaces.continuous_use_row_hash IS '不可变记录是否使用规范化整行哈希';
