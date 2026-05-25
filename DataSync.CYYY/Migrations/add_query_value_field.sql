-- 为 sync_task_interfaces 表新增 query_value_field 列
ALTER TABLE cyyy.sync_task_interfaces
ADD COLUMN IF NOT EXISTS query_value_field TEXT;
