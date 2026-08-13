CREATE TABLE IF NOT EXISTS cyyy.patient_continuous_sync_run_logs
(
    id BIGSERIAL PRIMARY KEY,
    task_id INT NOT NULL,
    session_id BIGINT,
    interface_id INT,
    patient_id TEXT,
    visit_sn TEXT,
    server_code TEXT,
    interface_name TEXT,
    level TEXT NOT NULL DEFAULT 'Info',
    message TEXT NOT NULL DEFAULT '',
    query_count INT NOT NULL DEFAULT 0,
    pushed_count INT NOT NULL DEFAULT 0,
    failed_count INT NOT NULL DEFAULT 0,
    window_from TIMESTAMP,
    window_to TIMESTAMP,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_patient_continuous_sync_run_logs_task_created
    ON cyyy.patient_continuous_sync_run_logs(task_id, created_at);
CREATE INDEX IF NOT EXISTS ix_patient_continuous_sync_run_logs_created
    ON cyyy.patient_continuous_sync_run_logs(created_at);

COMMENT ON TABLE cyyy.patient_continuous_sync_run_logs IS '患者持续同步运行日志';
