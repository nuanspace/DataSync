-- 组合子接口增加父子关联字段列表。
ALTER TABLE cyyy.sync_task_interfaces
    ADD COLUMN IF NOT EXISTS link_mappings TEXT;

COMMENT ON COLUMN cyyy.sync_task_interfaces.link_mappings IS '父子关联字段列表 JSON，仅子接口使用';
