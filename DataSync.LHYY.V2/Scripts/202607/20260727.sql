ALTER TABLE IF EXISTS lhyy.esb_interface_config
    ADD COLUMN IF NOT EXISTS main_record_array_path VARCHAR(500);
