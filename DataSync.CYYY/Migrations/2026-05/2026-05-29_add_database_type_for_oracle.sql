-- 采集源和同步接口增加数据库类型，支持 SqlServer / Oracle。
-- 日期：2026-05-29

ALTER TABLE cyyy.ingestion_sources
    ADD COLUMN IF NOT EXISTS database_type TEXT NOT NULL DEFAULT 'SqlServer';

UPDATE cyyy.ingestion_sources
SET database_type = 'SqlServer',
    source_type = 'Database'
WHERE source_type = 'SqlServer';

UPDATE cyyy.ingestion_sources
SET database_type = 'Oracle',
    source_type = 'Database'
WHERE source_type = 'Oracle';

COMMENT ON COLUMN cyyy.ingestion_sources.source_type IS '采集来源类型：DataLake / Database';
COMMENT ON COLUMN cyyy.ingestion_sources.database_type IS '数据库类型：SqlServer / Oracle';
COMMENT ON COLUMN cyyy.ingestion_sources.sql_server_host IS '数据库主机，SqlServer 可包含端口或实例名，Oracle 可填写主机或 host:port/service';
COMMENT ON COLUMN cyyy.ingestion_sources.sql_server_database IS '数据库名或 Oracle 服务名';
COMMENT ON COLUMN cyyy.ingestion_sources.sql_server_username IS '数据库用户名';
COMMENT ON COLUMN cyyy.ingestion_sources.sql_server_password IS '数据库密码';
COMMENT ON COLUMN cyyy.ingestion_sources.query_sql IS '数据库查询模板，SqlServer 支持 @from/@to，Oracle 支持 :from/:to 或 @from/@to';

ALTER TABLE cyyy.sync_task_interfaces
    ADD COLUMN IF NOT EXISTS database_type TEXT NOT NULL DEFAULT 'SqlServer';

UPDATE cyyy.sync_task_interfaces
SET database_type = 'SqlServer',
    source_type = 'Database'
WHERE source_type = 'SqlServer';

UPDATE cyyy.sync_task_interfaces
SET database_type = 'Oracle',
    source_type = 'Database'
WHERE source_type = 'Oracle';

COMMENT ON COLUMN cyyy.sync_task_interfaces.source_type IS '接口查询来源类型：DataLake / Database';
COMMENT ON COLUMN cyyy.sync_task_interfaces.database_type IS '数据库类型：SqlServer / Oracle';
COMMENT ON COLUMN cyyy.sync_task_interfaces.sql_server_host IS '数据库主机，SqlServer 可包含端口或实例名，Oracle 可填写主机或 host:port/service';
COMMENT ON COLUMN cyyy.sync_task_interfaces.sql_server_database IS '数据库名或 Oracle 服务名';
COMMENT ON COLUMN cyyy.sync_task_interfaces.sql_server_username IS '数据库用户名';
COMMENT ON COLUMN cyyy.sync_task_interfaces.sql_server_password IS '数据库密码';
COMMENT ON COLUMN cyyy.sync_task_interfaces.query_sql IS '数据库查询模板，SqlServer 支持 @queryValue，Oracle 支持 :queryValue 或 @queryValue';
