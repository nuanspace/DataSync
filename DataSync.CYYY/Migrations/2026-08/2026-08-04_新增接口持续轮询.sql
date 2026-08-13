ALTER TABLE cyyy.sync_task_interfaces
    ADD COLUMN IF NOT EXISTS continuous_polling_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS continuous_polling_interval_seconds INT NOT NULL DEFAULT 300,
    ADD COLUMN IF NOT EXISTS query_start_time_source_server_code TEXT,
    ADD COLUMN IF NOT EXISTS query_start_time_source_field TEXT,
    ADD COLUMN IF NOT EXISTS query_end_time_source_server_code TEXT,
    ADD COLUMN IF NOT EXISTS query_end_time_source_field TEXT;

ALTER TABLE cyyy.pending_sync_items
    ADD COLUMN IF NOT EXISTS continuous_interface_states TEXT NOT NULL DEFAULT '{}';

COMMENT ON COLUMN cyyy.sync_task_interfaces.continuous_polling_enabled IS '是否在患者住院期间持续轮询';
COMMENT ON COLUMN cyyy.sync_task_interfaces.continuous_polling_interval_seconds IS '持续轮询间隔秒数';
COMMENT ON COLUMN cyyy.sync_task_interfaces.query_start_time_source_server_code IS '入院时间来源采集接口编码';
COMMENT ON COLUMN cyyy.sync_task_interfaces.query_start_time_source_field IS '触发记录中的入院时间字段';
COMMENT ON COLUMN cyyy.sync_task_interfaces.query_end_time_source_server_code IS '出院时间来源采集接口编码';
COMMENT ON COLUMN cyyy.sync_task_interfaces.query_end_time_source_field IS '触发记录中的出院时间字段';
COMMENT ON COLUMN cyyy.pending_sync_items.continuous_interface_states IS '持续轮询接口状态 JSON';
