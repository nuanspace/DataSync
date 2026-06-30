ALTER TABLE IF EXISTS lhyy.esb_field_mapping
    ADD COLUMN IF NOT EXISTS sync_key VARCHAR(64);

ALTER TABLE IF EXISTS lhyy.esb_field_mapping
    ADD COLUMN IF NOT EXISTS last_sync_hash VARCHAR(64);

CREATE INDEX IF NOT EXISTS ix_esb_field_mapping_sync_key
    ON lhyy.esb_field_mapping (sync_key);
