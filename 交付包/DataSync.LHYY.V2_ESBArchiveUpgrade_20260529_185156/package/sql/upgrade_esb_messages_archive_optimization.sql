-- DATASYNC:NONTRANSACTIONAL
-- 海量消息归档与查询优化

CREATE SCHEMA IF NOT EXISTS lhyy;

CREATE TABLE IF NOT EXISTS lhyy.esb_messages_archive (
    id BIGINT NOT NULL,
    message_id VARCHAR(100) NOT NULL,
    source_message_id VARCHAR(100),
    tran_code VARCHAR(20) NOT NULL,
    integration_project_code VARCHAR(50),
    tran_name VARCHAR(100),
    app_id VARCHAR(50),
    org_id VARCHAR(50),
    esb_timestamp VARCHAR(50),
    raw_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    body_json JSONB,
    idempotent_key VARCHAR(200),
    mrn VARCHAR(100),
    visit_no VARCHAR(100),
    inpatient_no VARCHAR(100),
    resolved_event_time TIMESTAMP,
    matched_rule_group INT,
    status SMALLINT NOT NULL,
    retry_count INT NOT NULL DEFAULT 0,
    error_message TEXT,
    patient_id UUID,
    event_id UUID,
    processed_at TIMESTAMP,
    processing_started_at TIMESTAMP,
    created_at TIMESTAMP NOT NULL,
    archived_at TIMESTAMP NOT NULL DEFAULT NOW()
) PARTITION BY RANGE (created_at);

CREATE TABLE IF NOT EXISTS lhyy.esb_process_log_archive (
    id BIGINT NOT NULL,
    message_id BIGINT NOT NULL,
    integration_project_code VARCHAR(50),
    step VARCHAR(100) NOT NULL,
    is_success BOOLEAN NOT NULL,
    detail TEXT,
    elapsed_ms INT NOT NULL DEFAULT 0,
    created_at TIMESTAMP NOT NULL,
    archived_at TIMESTAMP NOT NULL DEFAULT NOW()
) PARTITION BY RANGE (created_at);

CREATE OR REPLACE FUNCTION lhyy.ensure_esb_archive_partition(p_month DATE)
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
    v_start DATE := date_trunc('month', p_month)::date;
    v_end DATE := (date_trunc('month', p_month) + interval '1 month')::date;
    v_msg_partition TEXT := 'esb_messages_archive_' || to_char(v_start, 'YYYYMM');
    v_log_partition TEXT := 'esb_process_log_archive_' || to_char(v_start, 'YYYYMM');
BEGIN
    EXECUTE format(
        'CREATE TABLE IF NOT EXISTS lhyy.%I PARTITION OF lhyy.esb_messages_archive FOR VALUES FROM (%L) TO (%L)',
        v_msg_partition,
        v_start,
        v_end);

    EXECUTE format(
        'CREATE TABLE IF NOT EXISTS lhyy.%I PARTITION OF lhyy.esb_process_log_archive FOR VALUES FROM (%L) TO (%L)',
        v_log_partition,
        v_start,
        v_end);
END;
$$;

CREATE OR REPLACE VIEW lhyy.esb_messages_all AS
SELECT
    id,
    message_id,
    source_message_id,
    tran_code,
    integration_project_code,
    tran_name,
    app_id,
    org_id,
    esb_timestamp,
    raw_json,
    body_json,
    idempotent_key,
    mrn,
    visit_no,
    inpatient_no,
    resolved_event_time,
    matched_rule_group,
    status,
    retry_count,
    error_message,
    patient_id,
    event_id,
    processed_at,
    processing_started_at,
    created_at
FROM lhyy.esb_messages
UNION ALL
SELECT
    id,
    message_id,
    source_message_id,
    tran_code,
    integration_project_code,
    tran_name,
    app_id,
    org_id,
    esb_timestamp,
    raw_json,
    body_json,
    idempotent_key,
    mrn,
    visit_no,
    inpatient_no,
    resolved_event_time,
    matched_rule_group,
    status,
    retry_count,
    error_message,
    patient_id,
    event_id,
    processed_at,
    processing_started_at,
    created_at
FROM lhyy.esb_messages_archive;

CREATE OR REPLACE VIEW lhyy.esb_process_log_all AS
SELECT
    id,
    message_id,
    integration_project_code,
    step,
    is_success,
    detail,
    elapsed_ms,
    created_at
FROM lhyy.esb_process_log
UNION ALL
SELECT
    id,
    message_id,
    integration_project_code,
    step,
    is_success,
    detail,
    elapsed_ms,
    created_at
FROM lhyy.esb_process_log_archive;

DO $$
DECLARE
    v_index_name TEXT;
BEGIN
    FOREACH v_index_name IN ARRAY ARRAY[
        'ix_esb_messages_project_created_id',
        'ix_esb_messages_project_status_created',
        'ix_esb_messages_project_tran_created',
        'ix_esb_messages_project_mrn_created',
        'ix_esb_messages_queue_claim',
        'ix_esb_messages_processing_timeout',
        'ux_esb_messages_archive_id_created_at',
        'ix_esb_messages_archive_id',
        'ix_esb_messages_archive_project_created_id',
        'ix_esb_messages_archive_project_status_created',
        'ix_esb_messages_archive_project_tran_created',
        'ix_esb_messages_archive_project_mrn_created',
        'ix_esb_messages_archive_mrn_event_time',
        'ix_esb_process_log_message_created',
        'ux_esb_process_log_archive_id_created_at',
        'ix_esb_process_log_archive_message_created',
        'ix_esb_process_log_archive_project_created'
    ]
    LOOP
        IF EXISTS (
            SELECT 1
            FROM pg_class c
            INNER JOIN pg_namespace n ON n.oid = c.relnamespace
            INNER JOIN pg_index i ON i.indexrelid = c.oid
            WHERE n.nspname = 'lhyy'
              AND c.relname = v_index_name
              AND i.indisvalid = FALSE
        ) THEN
            EXECUTE format('DROP INDEX %I.%I', 'lhyy', v_index_name);
        END IF;
    END LOOP;
END;
$$;

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_esb_messages_project_created_id
    ON lhyy.esb_messages (integration_project_code, created_at DESC, id DESC);
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_esb_messages_project_status_created
    ON lhyy.esb_messages (integration_project_code, status, created_at DESC);
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_esb_messages_project_tran_created
    ON lhyy.esb_messages (integration_project_code, tran_code, created_at DESC);
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_esb_messages_project_mrn_created
    ON lhyy.esb_messages (integration_project_code, mrn, created_at DESC);
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_esb_messages_queue_claim
    ON lhyy.esb_messages (status, retry_count, created_at, id)
    WHERE status IN (0, 3);
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_esb_messages_processing_timeout
    ON lhyy.esb_messages (processing_started_at)
    WHERE status = 1;

CREATE INDEX IF NOT EXISTS ix_esb_messages_archive_id
    ON lhyy.esb_messages_archive (id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_esb_messages_archive_id_created_at
    ON lhyy.esb_messages_archive (id, created_at);
CREATE INDEX IF NOT EXISTS ix_esb_messages_archive_project_created_id
    ON lhyy.esb_messages_archive (integration_project_code, created_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_esb_messages_archive_project_status_created
    ON lhyy.esb_messages_archive (integration_project_code, status, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_esb_messages_archive_project_tran_created
    ON lhyy.esb_messages_archive (integration_project_code, tran_code, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_esb_messages_archive_project_mrn_created
    ON lhyy.esb_messages_archive (integration_project_code, mrn, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_esb_messages_archive_mrn_event_time
    ON lhyy.esb_messages_archive (mrn, resolved_event_time);

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_esb_process_log_message_created
    ON lhyy.esb_process_log (message_id, created_at);
CREATE UNIQUE INDEX IF NOT EXISTS ux_esb_process_log_archive_id_created_at
    ON lhyy.esb_process_log_archive (id, created_at);
CREATE INDEX IF NOT EXISTS ix_esb_process_log_archive_message_created
    ON lhyy.esb_process_log_archive (message_id, created_at);
CREATE INDEX IF NOT EXISTS ix_esb_process_log_archive_project_created
    ON lhyy.esb_process_log_archive (integration_project_code, created_at);

INSERT INTO lhyy.esb_global_config (config_key, config_value, config_type, description)
SELECT 'MessageHotRetentionDays', '30', 'int', '消息热表保留天数；超过该天数的终态消息归档到历史分区表'
WHERE NOT EXISTS (
    SELECT 1
    FROM lhyy.esb_global_config
    WHERE config_key = 'MessageHotRetentionDays'
);

DO $$
DECLARE
    v_sequence_name TEXT;
    v_next_value BIGINT;
BEGIN
    v_sequence_name := pg_get_serial_sequence('lhyy.esb_messages', 'id');
    IF v_sequence_name IS NOT NULL THEN
        SELECT GREATEST(
            COALESCE((SELECT MAX(id) FROM lhyy.esb_messages), 0),
            COALESCE((SELECT MAX(id) FROM lhyy.esb_messages_archive), 0),
            1)
        INTO v_next_value;
        EXECUTE format('SELECT setval(%L, %s, true)', v_sequence_name, v_next_value);
    END IF;

    v_sequence_name := pg_get_serial_sequence('lhyy.esb_process_log', 'id');
    IF v_sequence_name IS NOT NULL THEN
        SELECT GREATEST(
            COALESCE((SELECT MAX(id) FROM lhyy.esb_process_log), 0),
            COALESCE((SELECT MAX(id) FROM lhyy.esb_process_log_archive), 0),
            1)
        INTO v_next_value;
        EXECUTE format('SELECT setval(%L, %s, true)', v_sequence_name, v_next_value);
    END IF;
END;
$$;
