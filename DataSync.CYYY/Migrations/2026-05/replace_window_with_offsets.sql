-- 将 window_minutes 替换为 start_offset_minutes + end_offset_minutes

-- 新增两个偏移量字段
ALTER TABLE cyyy.ingestion_sources
ADD COLUMN IF NOT EXISTS start_offset_minutes INTEGER NOT NULL DEFAULT 120;

ALTER TABLE cyyy.ingestion_sources
ADD COLUMN IF NOT EXISTS end_offset_minutes INTEGER NOT NULL DEFAULT 0;

-- 将旧 window_minutes 数据迁移到 start_offset_minutes
UPDATE cyyy.ingestion_sources
SET start_offset_minutes = window_minutes
WHERE window_minutes IS NOT NULL;

-- 删除旧字段
ALTER TABLE cyyy.ingestion_sources
DROP COLUMN IF EXISTS window_minutes;
