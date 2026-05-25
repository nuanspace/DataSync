-- 当前项目结构补齐脚本

CREATE SCHEMA IF NOT EXISTS lhyy;

ALTER TABLE IF EXISTS lhyy.esb_messages
    ADD COLUMN IF NOT EXISTS integration_project_code VARCHAR(50),
    ADD COLUMN IF NOT EXISTS processing_started_at TIMESTAMP;

ALTER TABLE IF EXISTS lhyy.esb_interface_config
    ADD COLUMN IF NOT EXISTS integration_project_code VARCHAR(50),
    ADD COLUMN IF NOT EXISTS mrn_source_path VARCHAR(500),
    ADD COLUMN IF NOT EXISTS event_start_time_source_path VARCHAR(500),
    ADD COLUMN IF NOT EXISTS sample_json TEXT;

ALTER TABLE IF EXISTS lhyy.esb_field_mapping
    ADD COLUMN IF NOT EXISTS integration_project_code VARCHAR(50);

ALTER TABLE IF EXISTS lhyy.esb_dict
    ADD COLUMN IF NOT EXISTS integration_project_code VARCHAR(50);

ALTER TABLE IF EXISTS lhyy.esb_process_log
    ADD COLUMN IF NOT EXISTS integration_project_code VARCHAR(50);

ALTER TABLE IF EXISTS lhyy.esb_filter_rule
    ADD COLUMN IF NOT EXISTS integration_project_code VARCHAR(50),
    ADD COLUMN IF NOT EXISTS filter_scope SMALLINT NOT NULL DEFAULT 0;

ALTER TABLE IF EXISTS lhyy.esb_interface_match_rule
    ADD COLUMN IF NOT EXISTS integration_project_code VARCHAR(50);

ALTER TABLE IF EXISTS lhyy.esb_idempotent_key_part
    ADD COLUMN IF NOT EXISTS integration_project_code VARCHAR(50);

ALTER TABLE IF EXISTS lhyy.esb_event_identity
    ADD COLUMN IF NOT EXISTS integration_project_code VARCHAR(50);

ALTER TABLE IF EXISTS lhyy.esb_message_receipt
    ADD COLUMN IF NOT EXISTS integration_project_code VARCHAR(50);

CREATE TABLE IF NOT EXISTS lhyy.esb_integration_project (
    id SERIAL PRIMARY KEY,
    project_code VARCHAR(50) NOT NULL DEFAULT '',
    project_name VARCHAR(100) NOT NULL DEFAULT '',
    is_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    description VARCHAR(500),
    sort_order INT NOT NULL DEFAULT 0,
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE lhyy.esb_integration_project IS '接入项目';

DROP INDEX IF EXISTS lhyy.ix_esb_interface_config_tran_code;
CREATE UNIQUE INDEX IF NOT EXISTS ix_esb_interface_config_tran_code_global
    ON lhyy.esb_interface_config (tran_code)
    WHERE integration_project_code IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ix_esb_interface_config_project_tran_code
    ON lhyy.esb_interface_config (integration_project_code, tran_code)
    WHERE integration_project_code IS NOT NULL;

DROP INDEX IF EXISTS lhyy.ix_esb_dict_code_source;
CREATE UNIQUE INDEX IF NOT EXISTS ix_esb_dict_code_source_global
    ON lhyy.esb_dict (dict_code, source_value)
    WHERE integration_project_code IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ix_esb_dict_project_code_source
    ON lhyy.esb_dict (integration_project_code, dict_code, source_value)
    WHERE integration_project_code IS NOT NULL;

DROP INDEX IF EXISTS lhyy.ix_esb_message_receipt_source_message_id;
DROP INDEX IF EXISTS lhyy.ix_esb_message_receipt_idempotent_key;
CREATE UNIQUE INDEX IF NOT EXISTS ix_esb_message_receipt_source_message_id
    ON lhyy.esb_message_receipt (integration_project_code, tran_code, source_message_id)
    WHERE source_message_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ix_esb_message_receipt_idempotent_key
    ON lhyy.esb_message_receipt (integration_project_code, tran_code, idempotent_key)
    WHERE idempotent_key IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_esb_messages_integration_project_code
    ON lhyy.esb_messages (integration_project_code);
CREATE INDEX IF NOT EXISTS ix_esb_field_mapping_integration_project_code
    ON lhyy.esb_field_mapping (integration_project_code);
CREATE INDEX IF NOT EXISTS ix_esb_process_log_integration_project_code
    ON lhyy.esb_process_log (integration_project_code);
CREATE INDEX IF NOT EXISTS ix_esb_filter_rule_integration_project_code
    ON lhyy.esb_filter_rule (integration_project_code);
CREATE INDEX IF NOT EXISTS ix_esb_filter_rule_mapping_id
    ON lhyy.esb_filter_rule (mapping_id);
CREATE INDEX IF NOT EXISTS ix_esb_interface_match_rule_integration_project_code
    ON lhyy.esb_interface_match_rule (integration_project_code);
CREATE INDEX IF NOT EXISTS ix_esb_interface_match_rule_tran_code_match_group
    ON lhyy.esb_interface_match_rule (tran_code, match_group);
CREATE INDEX IF NOT EXISTS ix_esb_idempotent_key_part_integration_project_code
    ON lhyy.esb_idempotent_key_part (integration_project_code);
CREATE INDEX IF NOT EXISTS ix_esb_event_identity_integration_project_code
    ON lhyy.esb_event_identity (integration_project_code);
DROP INDEX IF EXISTS lhyy.ix_esb_integration_project_project_code;
CREATE UNIQUE INDEX IF NOT EXISTS ix_esb_integration_project_project_code
    ON lhyy.esb_integration_project (project_code);
