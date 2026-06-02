-- 新增数据库资源，并把采集源和同步接口中的旧连接配置迁移为资源引用。

CREATE TABLE IF NOT EXISTS cyyy.database_resources (
    id                SERIAL PRIMARY KEY,
    name              TEXT NOT NULL,
    database_type     TEXT NOT NULL DEFAULT 'SqlServer',
    host              TEXT NOT NULL,
    database_name     TEXT NOT NULL,
    username          TEXT NOT NULL,
    password          TEXT,
    trust_certificate BOOLEAN NOT NULL DEFAULT TRUE,
    created_at        TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at        TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_database_resources_name
    ON cyyy.database_resources (name);

ALTER TABLE cyyy.ingestion_sources
    ADD COLUMN IF NOT EXISTS database_resource_id INT;

ALTER TABLE cyyy.sync_task_interfaces
    ADD COLUMN IF NOT EXISTS database_resource_id INT;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_ingestion_sources_database_resource'
    ) THEN
        ALTER TABLE cyyy.ingestion_sources
        ADD CONSTRAINT fk_ingestion_sources_database_resource
        FOREIGN KEY (database_resource_id)
        REFERENCES cyyy.database_resources(id)
        ON DELETE RESTRICT;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_sync_task_interfaces_database_resource'
    ) THEN
        ALTER TABLE cyyy.sync_task_interfaces
        ADD CONSTRAINT fk_sync_task_interfaces_database_resource
        FOREIGN KEY (database_resource_id)
        REFERENCES cyyy.database_resources(id)
        ON DELETE RESTRICT;
    END IF;
END $$;

WITH legacy_configs AS (
    SELECT
        COALESCE(NULLIF(database_type, ''), 'SqlServer') AS database_type,
        sql_server_host AS host,
        sql_server_database AS database_name,
        sql_server_username AS username,
        sql_server_password AS password,
        COALESCE(sql_server_trust_certificate, TRUE) AS trust_certificate
    FROM cyyy.ingestion_sources
    WHERE source_type = 'Database'
      AND database_resource_id IS NULL
      AND COALESCE(sql_server_host, '') <> ''
      AND COALESCE(sql_server_database, '') <> ''
      AND COALESCE(sql_server_username, '') <> ''

    UNION

    SELECT
        COALESCE(NULLIF(database_type, ''), 'SqlServer') AS database_type,
        sql_server_host AS host,
        sql_server_database AS database_name,
        sql_server_username AS username,
        sql_server_password AS password,
        COALESCE(sql_server_trust_certificate, TRUE) AS trust_certificate
    FROM cyyy.sync_task_interfaces
    WHERE source_type = 'Database'
      AND database_resource_id IS NULL
      AND COALESCE(sql_server_host, '') <> ''
      AND COALESCE(sql_server_database, '') <> ''
      AND COALESCE(sql_server_username, '') <> ''
),
normalized AS (
    SELECT DISTINCT
        database_type,
        host,
        database_name,
        username,
        password,
        trust_certificate
    FROM legacy_configs
)
INSERT INTO cyyy.database_resources (
    name,
    database_type,
    host,
    database_name,
    username,
    password,
    trust_certificate
)
SELECT
    database_type || ' ' || database_name || '@' || host || ' ' ||
        substr(md5(concat_ws('|', database_type, host, database_name, username, COALESCE(password, ''), trust_certificate::text)), 1, 8),
    database_type,
    host,
    database_name,
    username,
    password,
    trust_certificate
FROM normalized c
WHERE NOT EXISTS (
    SELECT 1
    FROM cyyy.database_resources r
    WHERE r.database_type = c.database_type
      AND r.host = c.host
      AND r.database_name = c.database_name
      AND r.username = c.username
      AND COALESCE(r.password, '') = COALESCE(c.password, '')
      AND r.trust_certificate = c.trust_certificate
)
ON CONFLICT (name) DO NOTHING;

UPDATE cyyy.ingestion_sources s
SET database_resource_id = r.id
FROM cyyy.database_resources r
WHERE s.source_type = 'Database'
  AND s.database_resource_id IS NULL
  AND r.database_type = COALESCE(NULLIF(s.database_type, ''), 'SqlServer')
  AND r.host = s.sql_server_host
  AND r.database_name = s.sql_server_database
  AND r.username = s.sql_server_username
  AND COALESCE(r.password, '') = COALESCE(s.sql_server_password, '')
  AND r.trust_certificate = COALESCE(s.sql_server_trust_certificate, TRUE);

UPDATE cyyy.sync_task_interfaces i
SET database_resource_id = r.id
FROM cyyy.database_resources r
WHERE i.source_type = 'Database'
  AND i.database_resource_id IS NULL
  AND COALESCE(i.sql_server_host, '') <> ''
  AND r.database_type = COALESCE(NULLIF(i.database_type, ''), 'SqlServer')
  AND r.host = i.sql_server_host
  AND r.database_name = i.sql_server_database
  AND r.username = i.sql_server_username
  AND COALESCE(r.password, '') = COALESCE(i.sql_server_password, '')
  AND r.trust_certificate = COALESCE(i.sql_server_trust_certificate, TRUE);
