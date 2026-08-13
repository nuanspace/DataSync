ALTER TABLE cyyy.sync_task_interfaces
    ADD COLUMN IF NOT EXISTS continuous_use_time_range BOOLEAN NOT NULL DEFAULT TRUE;

COMMENT ON COLUMN cyyy.sync_task_interfaces.continuous_use_time_range IS '患者持续同步查询是否携带开始、结束时间';
