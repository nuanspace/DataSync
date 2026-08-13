ALTER TABLE cyyy.sync_task_interfaces
    ADD COLUMN IF NOT EXISTS access_window_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS access_window_start VARCHAR(5),
    ADD COLUMN IF NOT EXISTS access_window_end VARCHAR(5);

ALTER TABLE cyyy.pending_sync_items
    ADD COLUMN IF NOT EXISTS completed_interface_keys TEXT NOT NULL DEFAULT '[]';

ALTER TABLE cyyy.active_sync_case_source_states
    ADD COLUMN IF NOT EXISTS pending_case_json TEXT,
    ADD COLUMN IF NOT EXISTS retry_count INTEGER NOT NULL DEFAULT 0;

COMMENT ON COLUMN cyyy.sync_task_interfaces.access_window_enabled IS '是否仅在指定每日时间段访问';
COMMENT ON COLUMN cyyy.sync_task_interfaces.access_window_start IS '每日允许访问开始时间，HH:mm';
COMMENT ON COLUMN cyyy.sync_task_interfaces.access_window_end IS '每日允许访问结束时间，HH:mm';
COMMENT ON COLUMN cyyy.pending_sync_items.completed_interface_keys IS '已完成顶层接口键 JSON';
COMMENT ON COLUMN cyyy.active_sync_case_source_states.pending_case_json IS '待执行 Active 病例快照 JSON';
COMMENT ON COLUMN cyyy.active_sync_case_source_states.retry_count IS '允许时段内真实失败次数';