-- 采集源增加额外查询条件字段
ALTER TABLE cyyy.ingestion_sources
ADD COLUMN IF NOT EXISTS conditions TEXT;
