ALTER TABLE cyyy.active_sync_tasks
    ADD COLUMN IF NOT EXISTS patient_id_source TEXT NOT NULL DEFAULT 'Mrn';

ALTER TABLE cyyy.active_sync_tasks
    ADD COLUMN IF NOT EXISTS visit_sn_source TEXT NOT NULL DEFAULT 'InpatientNo';
