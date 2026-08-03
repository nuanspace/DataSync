-- 采集源偏移量单位，偏移量数值仍按分钟存储。
ALTER TABLE cyyy.ingestion_sources
ADD COLUMN IF NOT EXISTS start_offset_unit VARCHAR(20) NOT NULL DEFAULT 'hours';

ALTER TABLE cyyy.ingestion_sources
ADD COLUMN IF NOT EXISTS end_offset_unit VARCHAR(20) NOT NULL DEFAULT 'hours';

UPDATE cyyy.ingestion_sources
SET start_offset_unit = CASE WHEN MOD(start_offset_minutes, 60) = 0 THEN 'hours' ELSE 'minutes' END,
    end_offset_unit = CASE WHEN MOD(end_offset_minutes, 60) = 0 THEN 'hours' ELSE 'minutes' END;
