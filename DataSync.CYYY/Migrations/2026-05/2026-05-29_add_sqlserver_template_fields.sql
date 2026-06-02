-- SQL Server SQL 模板来源配置字段
-- 日期：2026-05-29

ALTER TABLE cyyy.ingestion_sources
    ADD COLUMN IF NOT EXISTS source_type TEXT NOT NULL DEFAULT 'DataLake',
    ADD COLUMN IF NOT EXISTS connection_string_name TEXT,
    ADD COLUMN IF NOT EXISTS sql_server_host TEXT,
    ADD COLUMN IF NOT EXISTS sql_server_database TEXT,
    ADD COLUMN IF NOT EXISTS sql_server_username TEXT,
    ADD COLUMN IF NOT EXISTS sql_server_password TEXT,
    ADD COLUMN IF NOT EXISTS sql_server_trust_certificate BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS query_sql TEXT,
    ADD COLUMN IF NOT EXISTS lookback_minutes INT NOT NULL DEFAULT 5;

COMMENT ON COLUMN cyyy.ingestion_sources.source_type IS '采集来源类型：DataLake / SqlServer';
COMMENT ON COLUMN cyyy.ingestion_sources.connection_string_name IS '旧版兼容字段：连接串名称或完整连接串';
COMMENT ON COLUMN cyyy.ingestion_sources.sql_server_host IS 'SQL Server 主机，可包含端口或实例名';
COMMENT ON COLUMN cyyy.ingestion_sources.sql_server_database IS 'SQL Server 默认数据库';
COMMENT ON COLUMN cyyy.ingestion_sources.sql_server_username IS 'SQL Server 用户名';
COMMENT ON COLUMN cyyy.ingestion_sources.sql_server_password IS 'SQL Server 密码';
COMMENT ON COLUMN cyyy.ingestion_sources.sql_server_trust_certificate IS '是否信任 SQL Server 服务器证书';
COMMENT ON COLUMN cyyy.ingestion_sources.query_sql IS 'SQL Server 主查询模板，支持 @from 和 @to 参数';
COMMENT ON COLUMN cyyy.ingestion_sources.lookback_minutes IS '检查点回看分钟数';

ALTER TABLE cyyy.sync_task_interfaces
    ADD COLUMN IF NOT EXISTS source_type TEXT NOT NULL DEFAULT 'DataLake',
    ADD COLUMN IF NOT EXISTS connection_string_name TEXT,
    ADD COLUMN IF NOT EXISTS sql_server_host TEXT,
    ADD COLUMN IF NOT EXISTS sql_server_database TEXT,
    ADD COLUMN IF NOT EXISTS sql_server_username TEXT,
    ADD COLUMN IF NOT EXISTS sql_server_password TEXT,
    ADD COLUMN IF NOT EXISTS sql_server_trust_certificate BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS query_sql TEXT;

COMMENT ON COLUMN cyyy.sync_task_interfaces.source_type IS '接口查询来源类型：DataLake / SqlServer';
COMMENT ON COLUMN cyyy.sync_task_interfaces.connection_string_name IS '旧版兼容字段：连接串名称或完整连接串';
COMMENT ON COLUMN cyyy.sync_task_interfaces.sql_server_host IS 'SQL Server 主机，可包含端口或实例名';
COMMENT ON COLUMN cyyy.sync_task_interfaces.sql_server_database IS 'SQL Server 默认数据库';
COMMENT ON COLUMN cyyy.sync_task_interfaces.sql_server_username IS 'SQL Server 用户名';
COMMENT ON COLUMN cyyy.sync_task_interfaces.sql_server_password IS 'SQL Server 密码';
COMMENT ON COLUMN cyyy.sync_task_interfaces.sql_server_trust_certificate IS '是否信任 SQL Server 服务器证书';
COMMENT ON COLUMN cyyy.sync_task_interfaces.query_sql IS 'SQL Server 查询模板，支持 @queryValue 参数';
