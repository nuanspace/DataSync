-- 前置条件：已执行 Scripts/init.sql 及本文件之前的迁移。
-- 执行本文件后，应用启动不再自动创建或修改业务表。

CREATE TABLE IF NOT EXISTS cyyy.data_lake_configs (
    "Id"                          SERIAL PRIMARY KEY,
    "BaseUrl"                     VARCHAR(500) NOT NULL,
    "TokenEndpoint"               VARCHAR(200) NOT NULL DEFAULT '/auth/oauth/token',
    "QueryEndpoint"               VARCHAR(200) NOT NULL DEFAULT '/api/jhids4s/common/server/dataQuery',
    "ClientId"                    VARCHAR(200) NOT NULL DEFAULT '',
    "ClientSecret"                VARCHAR(500) NOT NULL DEFAULT '',
    "SysCode"                     VARCHAR(100) NOT NULL DEFAULT 'client-app',
    "PageSize"                    INT NOT NULL DEFAULT 100,
    "MaxResultSize"               INT NOT NULL DEFAULT 10000,
    "RequestIntervalMilliseconds" INT NOT NULL DEFAULT 200,
    "UpdatedAt"                   TIMESTAMP NOT NULL DEFAULT NOW(),
    "DebugLogEnabled"             BOOLEAN NOT NULL DEFAULT TRUE
);

ALTER TABLE cyyy.data_lake_configs
    ADD COLUMN IF NOT EXISTS "RequestIntervalMilliseconds" INT NOT NULL DEFAULT 200,
    ADD COLUMN IF NOT EXISTS "DebugLogEnabled" BOOLEAN NOT NULL DEFAULT TRUE;

CREATE TABLE IF NOT EXISTS cyyy.data_lake_interfaces (
    id          SERIAL PRIMARY KEY,
    server_code TEXT NOT NULL,
    name        TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_data_lake_interfaces_server_code
    ON cyyy.data_lake_interfaces (server_code);

ALTER TABLE cyyy.sync_tasks
    ADD COLUMN IF NOT EXISTS enable_trigger_record_push BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS trigger_push_target TEXT,
    ADD COLUMN IF NOT EXISTS trigger_push_params TEXT;

ALTER TABLE cyyy.sync_task_interfaces
    ADD COLUMN IF NOT EXISTS interface_key TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS parent_interface_key TEXT,
    ADD COLUMN IF NOT EXISTS parent_result_field TEXT,
    ADD COLUMN IF NOT EXISTS mount_field TEXT,
    ADD COLUMN IF NOT EXISTS route_field TEXT,
    ADD COLUMN IF NOT EXISTS route_operator TEXT,
    ADD COLUMN IF NOT EXISTS route_value TEXT,
    ADD COLUMN IF NOT EXISTS output_fields TEXT;

UPDATE cyyy.sync_task_interfaces
SET interface_key = 'iface_' || id::text
WHERE interface_key = '';

UPDATE cyyy.sync_task_interfaces
SET route_operator = 'eq'
WHERE COALESCE(route_operator, '') = '';

CREATE UNIQUE INDEX IF NOT EXISTS ix_sync_task_interfaces_task_interface_key
    ON cyyy.sync_task_interfaces (task_id, interface_key);

ALTER TABLE cyyy.sync_logs
    ADD COLUMN IF NOT EXISTS server_code TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS interface_name TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS source_record_key TEXT;

CREATE INDEX IF NOT EXISTS ix_sync_logs_task_server
    ON cyyy.sync_logs (task_code, server_code);

CREATE INDEX IF NOT EXISTS ix_sync_logs_task_source_record
    ON cyyy.sync_logs (task_code, source_record_key);

CREATE TABLE IF NOT EXISTS cyyy.pending_sync_items (
    id                    BIGSERIAL PRIMARY KEY,
    task_code             VARCHAR(50)  NOT NULL,
    source_server_code    VARCHAR(100) NOT NULL,
    source_record_key     TEXT         NOT NULL,
    object_key            TEXT         NOT NULL DEFAULT '',
    his_pat_id            TEXT         NOT NULL DEFAULT '',
    pat_visit_sn          TEXT         NOT NULL DEFAULT '',
    pat_name              TEXT         NOT NULL DEFAULT '',
    trigger_record_json   TEXT         NOT NULL DEFAULT '{}',
    trigger_push_done     BOOLEAN      NOT NULL DEFAULT FALSE,
    trigger_push_done_at  TIMESTAMP,
    trigger_push_error    TEXT,
    status                VARCHAR(20)  NOT NULL DEFAULT 'Pending',
    retry_count           INT          NOT NULL DEFAULT 0,
    last_error            TEXT,
    next_retry_time       TIMESTAMP,
    last_started_at       TIMESTAMP,
    last_completed_at     TIMESTAMP,
    created_at            TIMESTAMP    NOT NULL DEFAULT NOW(),
    updated_at            TIMESTAMP    NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_pending_sync_items_source UNIQUE (task_code, source_record_key)
);

ALTER TABLE cyyy.pending_sync_items
    ADD COLUMN IF NOT EXISTS object_key TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS his_pat_id TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS pat_visit_sn TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS pat_name TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS trigger_push_done BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS trigger_push_done_at TIMESTAMP,
    ADD COLUMN IF NOT EXISTS trigger_push_error TEXT;

UPDATE cyyy.pending_sync_items AS p
SET his_pat_id = COALESCE(NULLIF(p.trigger_record_json::jsonb ->> st.patient_id_field, ''), p.his_pat_id, ''),
    pat_visit_sn = CASE
        WHEN COALESCE(st.visit_sn_field, '') = '' THEN ''
        ELSE COALESCE(p.trigger_record_json::jsonb ->> st.visit_sn_field, p.pat_visit_sn, '')
    END,
    pat_name = COALESCE(NULLIF(p.trigger_record_json::jsonb ->> 'PAT_NAME', ''), p.pat_name, ''),
    object_key = CASE
        WHEN COALESCE(NULLIF(p.trigger_record_json::jsonb ->> st.patient_id_field, ''), p.his_pat_id, '') = '' THEN p.object_key
        WHEN COALESCE(st.visit_sn_field, '') = '' THEN
            'PAT:' || replace(replace(replace(COALESCE(NULLIF(p.trigger_record_json::jsonb ->> st.patient_id_field, ''), p.his_pat_id, ''), '\', '\\'), '|', '\|'), '=', '\=')
        ELSE
            'PAT:' || replace(replace(replace(COALESCE(NULLIF(p.trigger_record_json::jsonb ->> st.patient_id_field, ''), p.his_pat_id, ''), '\', '\\'), '|', '\|'), '=', '\=')
            || '|VISIT:' ||
            replace(replace(replace(COALESCE(p.trigger_record_json::jsonb ->> st.visit_sn_field, p.pat_visit_sn, ''), '\', '\\'), '|', '\|'), '=', '\=')
    END
FROM cyyy.sync_tasks AS st
WHERE st.code = p.task_code
  AND (
      p.object_key = ''
      OR p.his_pat_id = ''
      OR p.pat_name = ''
      OR (COALESCE(st.visit_sn_field, '') <> '' AND p.pat_visit_sn = '')
  );

CREATE INDEX IF NOT EXISTS ix_pending_sync_items_task_status_retry
    ON cyyy.pending_sync_items (task_code, status, next_retry_time);

CREATE INDEX IF NOT EXISTS ix_pending_sync_items_task_object
    ON cyyy.pending_sync_items (task_code, object_key);

CREATE INDEX IF NOT EXISTS ix_pending_sync_items_task_patient_status
    ON cyyy.pending_sync_items (task_code, his_pat_id, pat_visit_sn, status);

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'uq_pending_sync_items_object'
    ) THEN
        ALTER TABLE cyyy.pending_sync_items
        DROP CONSTRAINT uq_pending_sync_items_object;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'uq_pending_sync_items_source'
    ) THEN
        BEGIN
            ALTER TABLE cyyy.pending_sync_items
            ADD CONSTRAINT uq_pending_sync_items_source UNIQUE (task_code, source_record_key);
        EXCEPTION
            WHEN duplicate_table OR duplicate_object OR unique_violation THEN
                NULL;
        END;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_active_sync_record_receipts_pushed_at
    ON cyyy.active_sync_record_receipts (pushed_at);
