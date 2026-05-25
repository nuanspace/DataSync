-- 为 sync_task_interfaces 表添加路由参数列
ALTER TABLE cyyy.sync_task_interfaces ADD COLUMN IF NOT EXISTS push_params TEXT;
