CREATE TABLE IF NOT EXISTS cyyy.ingestion_logs (
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

CREATE INDEX IF NOT EXISTS ix_ingestion_logs_created_at
    ON cyyy.ingestion_logs (created_at);

CREATE INDEX IF NOT EXISTS ix_ingestion_logs_server_created
    ON cyyy.ingestion_logs (server_code, created_at);

CREATE INDEX IF NOT EXISTS ix_ingestion_logs_server_success
    ON cyyy.ingestion_logs (server_code, success);
