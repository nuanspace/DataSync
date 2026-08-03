ALTER TABLE cyyy.ingestion_sources
ADD COLUMN IF NOT EXISTS query_path TEXT;

COMMENT ON COLUMN cyyy.ingestion_sources.query_path IS 'DynamicApi 采集查询端点前缀后的相对路径';
COMMENT ON COLUMN cyyy.ingestion_sources.source_type IS '采集来源类型：DataLake / DynamicApi / Database';
