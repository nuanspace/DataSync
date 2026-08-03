ALTER TABLE cyyy.sync_tasks
ADD COLUMN IF NOT EXISTS additional_trigger_server_codes TEXT NOT NULL DEFAULT '';

COMMENT ON COLUMN cyyy.sync_tasks.additional_trigger_server_codes IS '附加触发采集源编码，多个编码使用逗号分隔';
