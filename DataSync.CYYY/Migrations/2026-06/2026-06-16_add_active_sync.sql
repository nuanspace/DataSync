CREATE TABLE IF NOT EXISTS cyyy.active_sync_tasks (
    id                         SERIAL PRIMARY KEY,
    name                       TEXT NOT NULL,
    code                       TEXT NOT NULL,
    enabled                    BOOLEAN NOT NULL DEFAULT TRUE,
    sync_task_id               INT REFERENCES cyyy.sync_tasks(id) ON DELETE RESTRICT,
    active_records_url         TEXT NOT NULL DEFAULT '',
    integration_project_code   TEXT,
    push_type                  TEXT NOT NULL DEFAULT 'Api',
    push_target                TEXT NOT NULL DEFAULT '',
    case_batch_size            INT NOT NULL DEFAULT 50,
    concurrency                INT NOT NULL DEFAULT 3,
    polling_interval_seconds   INT NOT NULL DEFAULT 300,
    empty_backoff_base_seconds INT NOT NULL DEFAULT 1800,
    empty_backoff_max_seconds  INT NOT NULL DEFAULT 7200,
    remark                     TEXT
);

CREATE TABLE IF NOT EXISTS cyyy.active_sync_sources (
    id                         SERIAL PRIMARY KEY,
    task_id                    INT NOT NULL REFERENCES cyyy.active_sync_tasks(id) ON DELETE CASCADE,
    sync_task_interface_id     INT REFERENCES cyyy.sync_task_interfaces(id) ON DELETE RESTRICT,
    data_type                  TEXT NOT NULL DEFAULT '',
    server_code                TEXT NOT NULL,
    display_name               TEXT NOT NULL DEFAULT '',
    database_resource_id       INT REFERENCES cyyy.database_resources(id) ON DELETE RESTRICT,
    query_sql                  TEXT NOT NULL,
    inpatient_no_parameter     TEXT NOT NULL DEFAULT 'inpatientNo',
    admission_time_parameter   TEXT DEFAULT 'admissionTime',
    source_record_key_fields   TEXT NOT NULL DEFAULT '',
    polling_interval_seconds   INT NOT NULL DEFAULT 1800,
    sort_order                 INT NOT NULL DEFAULT 0,
    enabled                    BOOLEAN NOT NULL DEFAULT TRUE
);

ALTER TABLE cyyy.active_sync_tasks
    ADD COLUMN IF NOT EXISTS sync_task_id INT;

ALTER TABLE cyyy.active_sync_sources
    ADD COLUMN IF NOT EXISTS sync_task_interface_id INT;

ALTER TABLE cyyy.active_sync_sources
    ALTER COLUMN database_resource_id DROP NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_active_sync_tasks_sync_task'
    ) THEN
        ALTER TABLE cyyy.active_sync_tasks
        ADD CONSTRAINT fk_active_sync_tasks_sync_task
        FOREIGN KEY (sync_task_id)
        REFERENCES cyyy.sync_tasks(id)
        ON DELETE RESTRICT;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_active_sync_sources_sync_task_interface'
    ) THEN
        ALTER TABLE cyyy.active_sync_sources
        ADD CONSTRAINT fk_active_sync_sources_sync_task_interface
        FOREIGN KEY (sync_task_interface_id)
        REFERENCES cyyy.sync_task_interfaces(id)
        ON DELETE RESTRICT;
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS cyyy.active_sync_case_source_states (
    id                 BIGSERIAL PRIMARY KEY,
    task_id            INT NOT NULL,
    source_id          INT NOT NULL,
    inpatient_no       TEXT NOT NULL,
    empty_count        INT NOT NULL DEFAULT 0,
    next_query_time    TIMESTAMP,
    last_query_at      TIMESTAMP,
    last_result_count  INT NOT NULL DEFAULT 0,
    last_error         TEXT,
    updated_at         TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS cyyy.active_sync_record_receipts (
    id                 BIGSERIAL PRIMARY KEY,
    task_id            INT NOT NULL,
    source_id          INT NOT NULL,
    inpatient_no       TEXT NOT NULL,
    source_record_key  TEXT NOT NULL,
    pushed_at          TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS cyyy.active_sync_run_logs (
    id                 BIGSERIAL PRIMARY KEY,
    task_id            INT NOT NULL,
    source_id          INT,
    task_name          TEXT NOT NULL DEFAULT '',
    source_name        TEXT,
    inpatient_no       TEXT,
    level              TEXT NOT NULL DEFAULT 'Info',
    message            TEXT NOT NULL DEFAULT '',
    active_case_count  INT NOT NULL DEFAULT 0,
    query_count        INT NOT NULL DEFAULT 0,
    pushed_count       INT NOT NULL DEFAULT 0,
    created_at         TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_active_sync_tasks_code
    ON cyyy.active_sync_tasks (code);

CREATE INDEX IF NOT EXISTS ix_active_sync_tasks_sync_task
    ON cyyy.active_sync_tasks (sync_task_id);

CREATE INDEX IF NOT EXISTS ix_active_sync_sources_task_server
    ON cyyy.active_sync_sources (task_id, server_code);

CREATE INDEX IF NOT EXISTS ix_active_sync_sources_task_interface
    ON cyyy.active_sync_sources (task_id, sync_task_interface_id);

CREATE UNIQUE INDEX IF NOT EXISTS ix_active_sync_state_task_source_inpatient
    ON cyyy.active_sync_case_source_states (task_id, source_id, inpatient_no);

CREATE INDEX IF NOT EXISTS ix_active_sync_state_source_next
    ON cyyy.active_sync_case_source_states (source_id, next_query_time);

CREATE UNIQUE INDEX IF NOT EXISTS ix_active_sync_receipt_key
    ON cyyy.active_sync_record_receipts (task_id, source_id, inpatient_no, source_record_key);

CREATE INDEX IF NOT EXISTS ix_active_sync_run_logs_task_created
    ON cyyy.active_sync_run_logs (task_id, created_at);

CREATE INDEX IF NOT EXISTS ix_active_sync_run_logs_created
    ON cyyy.active_sync_run_logs (created_at);
