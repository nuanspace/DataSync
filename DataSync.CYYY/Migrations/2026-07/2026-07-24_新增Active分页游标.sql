ALTER TABLE cyyy.active_sync_tasks
    ADD COLUMN IF NOT EXISTS last_cursor BIGINT;
