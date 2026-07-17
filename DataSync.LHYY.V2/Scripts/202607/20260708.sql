-- FollowUp 医院数据回传管理表升级脚本
-- 合并来源：2026-07-08_datasync_cyyy_followup_package_sync.sql、2026-07-08_datasync_lhyy_followup_package_import.sql
-- CYYY 与 LHYY 共用 DataSyncDb，按 cyyy、lhyy schema 顺序创建管理表。

-- DataSync.CYYY FollowUp 医院数据包拉取管理表
-- 用途：开发调试阶段使用，正式完成后由 DataSync 负责人同步 EF model。

CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE SCHEMA IF NOT EXISTS cyyy;

CREATE TABLE IF NOT EXISTS cyyy.followup_package_source_config (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    hospital_code text NOT NULL,
    hospital_name text NOT NULL DEFAULT '',
    is_enabled boolean NOT NULL DEFAULT false,
    dmz_host text NOT NULL DEFAULT '',
    dmz_port integer NOT NULL DEFAULT 22,
    dmz_user text NOT NULL DEFAULT '',
    package_root text NOT NULL DEFAULT '',
    pull_interval_seconds integer NOT NULL DEFAULT 300,
    pull_policy_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    security_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    remark text,
    created_at timestamp without time zone NOT NULL DEFAULT now(),
    updated_at timestamp without time zone NOT NULL DEFAULT now(),
    CONSTRAINT uq_followup_package_source_config_hospital UNIQUE (hospital_code)
);

COMMENT ON TABLE cyyy.followup_package_source_config IS 'FollowUp 医院数据包拉取配置。开发阶段 SQL 访问，EF model 同步后改为 DbContext DbSet。';
COMMENT ON COLUMN cyyy.followup_package_source_config.security_json IS 'DMZ SSH、公钥指纹、token 策略摘要；不得保存 token 明文或私钥';

CREATE INDEX IF NOT EXISTS ix_followup_package_source_config_enabled
    ON cyyy.followup_package_source_config (is_enabled, hospital_code);

CREATE TABLE IF NOT EXISTS cyyy.followup_package_pull_state (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    hospital_code text NOT NULL,
    package_id text NOT NULL,
    sequence_no bigint NOT NULL,
    package_type text NOT NULL DEFAULT 'Incremental',
    trigger_type text NOT NULL DEFAULT 'Scheduled',
    pull_status text NOT NULL DEFAULT 'Pending',
    from_watermark timestamp without time zone,
    to_watermark timestamp without time zone,
    previous_package_id text,
    package_hash text,
    size_bytes bigint NOT NULL DEFAULT 0,
    local_package_path text NOT NULL DEFAULT '',
    schema_summary_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    package_summary_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    error_code text,
    error_message text,
    retry_count integer NOT NULL DEFAULT 0,
    next_retry_at timestamp without time zone,
    first_pulled_at timestamp without time zone,
    last_pulled_at timestamp without time zone,
    created_at timestamp without time zone NOT NULL DEFAULT now(),
    updated_at timestamp without time zone NOT NULL DEFAULT now(),
    CONSTRAINT uq_followup_package_pull_state_package UNIQUE (hospital_code, package_id),
    CONSTRAINT uq_followup_package_pull_state_sequence UNIQUE (hospital_code, sequence_no)
);

COMMENT ON TABLE cyyy.followup_package_pull_state IS 'FollowUp 医院数据包拉取状态。包完成落盘后才能置为 Pulled。';
COMMENT ON COLUMN cyyy.followup_package_pull_state.schema_summary_json IS '结构摘要，如 schemaSnapshotHash、tableManifestHash、schemaDiffLevel、requiresSchemaReview';
COMMENT ON COLUMN cyyy.followup_package_pull_state.package_summary_json IS '云端 relay-list 返回的非敏感包摘要';

CREATE INDEX IF NOT EXISTS ix_followup_package_pull_state_status
    ON cyyy.followup_package_pull_state (hospital_code, pull_status, sequence_no);

CREATE INDEX IF NOT EXISTS ix_followup_package_pull_state_retry
    ON cyyy.followup_package_pull_state (pull_status, next_retry_at)
    WHERE pull_status IN ('Pending', 'Failed');

CREATE TABLE IF NOT EXISTS cyyy.followup_package_ack_queue (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    hospital_code text NOT NULL,
    package_id text NOT NULL,
    ack_status text NOT NULL,
    ack_payload_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    forward_status text NOT NULL DEFAULT 'Pending',
    forward_error_code text,
    forward_error_message text,
    retry_count integer NOT NULL DEFAULT 0,
    next_retry_at timestamp without time zone,
    created_at timestamp without time zone NOT NULL DEFAULT now(),
    forwarded_at timestamp without time zone,
    updated_at timestamp without time zone NOT NULL DEFAULT now(),
    CONSTRAINT uq_followup_package_ack_queue_package_status UNIQUE (hospital_code, package_id, ack_status)
);

COMMENT ON TABLE cyyy.followup_package_ack_queue IS 'FollowUp 医院数据包导入回执转发队列。ack 不包含患者明细。';

CREATE INDEX IF NOT EXISTS ix_followup_package_ack_queue_forward
    ON cyyy.followup_package_ack_queue (forward_status, next_retry_at);

CREATE TABLE IF NOT EXISTS cyyy.followup_package_pull_log (
    id bigserial PRIMARY KEY,
    hospital_code text,
    package_id text,
    operation text NOT NULL,
    level text NOT NULL DEFAULT 'Info',
    message text NOT NULL DEFAULT '',
    detail_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamp without time zone NOT NULL DEFAULT now()
);

COMMENT ON TABLE cyyy.followup_package_pull_log IS 'FollowUp 医院数据包拉取和 ack 转发日志。禁止记录 token 明文和患者明细。';

CREATE INDEX IF NOT EXISTS ix_followup_package_pull_log_hospital_created
    ON cyyy.followup_package_pull_log (hospital_code, created_at DESC);

CREATE INDEX IF NOT EXISTS ix_followup_package_pull_log_package
    ON cyyy.followup_package_pull_log (package_id, created_at DESC);

-- 外键约束：仅约束本次新增表之间的强一致关系。
-- 日志表保留弱关联，避免失败日志因主记录尚未创建而写入失败。
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_followup_package_pull_state_source_config'
    ) THEN
        ALTER TABLE cyyy.followup_package_pull_state
            ADD CONSTRAINT fk_followup_package_pull_state_source_config
            FOREIGN KEY (hospital_code)
            REFERENCES cyyy.followup_package_source_config (hospital_code)
            ON UPDATE CASCADE
            ON DELETE RESTRICT;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_followup_package_pull_state_previous'
    ) THEN
        ALTER TABLE cyyy.followup_package_pull_state
            ADD CONSTRAINT fk_followup_package_pull_state_previous
            FOREIGN KEY (hospital_code, previous_package_id)
            REFERENCES cyyy.followup_package_pull_state (hospital_code, package_id)
            ON UPDATE CASCADE
            ON DELETE RESTRICT;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_followup_package_ack_queue_pull_state'
    ) THEN
        ALTER TABLE cyyy.followup_package_ack_queue
            ADD CONSTRAINT fk_followup_package_ack_queue_pull_state
            FOREIGN KEY (hospital_code, package_id)
            REFERENCES cyyy.followup_package_pull_state (hospital_code, package_id)
            ON UPDATE CASCADE
            ON DELETE RESTRICT;
    END IF;
END $$;

-- ============================================================================
-- DataSync.LHYY.V2 FollowUp 管理表
-- ============================================================================

-- DataSync.LHYY.V2 FollowUp 医院数据包导入管理表
-- 用途：开发调试阶段使用，正式完成后由 DataSync 负责人同步 EF model。

CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE SCHEMA IF NOT EXISTS lhyy;

CREATE TABLE IF NOT EXISTS lhyy.followup_package_import_state (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    hospital_code text NOT NULL,
    package_id text NOT NULL,
    sequence_no bigint NOT NULL,
    package_type text NOT NULL DEFAULT 'Incremental',
    import_status text NOT NULL DEFAULT 'Pending',
    from_watermark timestamp without time zone,
    to_watermark timestamp without time zone,
    previous_package_id text,
    package_hash text,
    local_package_path text NOT NULL DEFAULT '',
    staging_path text,
    export_contract_version text,
    min_importer_version text,
    importer_version text,
    schema_check_status text,
    schema_diff_level text,
    requires_schema_review boolean NOT NULL DEFAULT false,
    table_manifest_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    schema_snapshot_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    schema_diff_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    import_summary_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    error_code text,
    error_message text,
    started_at timestamp without time zone,
    finished_at timestamp without time zone,
    created_at timestamp without time zone NOT NULL DEFAULT now(),
    updated_at timestamp without time zone NOT NULL DEFAULT now(),
    CONSTRAINT uq_followup_package_import_state_package UNIQUE (hospital_code, package_id),
    CONSTRAINT uq_followup_package_import_state_sequence UNIQUE (hospital_code, sequence_no)
);

COMMENT ON TABLE lhyy.followup_package_import_state IS 'FollowUp 医院数据包导入状态。开发阶段 SQL 访问，EF model 同步后改为 DbContext DbSet。';
COMMENT ON COLUMN lhyy.followup_package_import_state.table_manifest_json IS '包内完整备份表清单，导入时必须以此为准';
COMMENT ON COLUMN lhyy.followup_package_import_state.schema_snapshot_json IS '包内表结构快照';
COMMENT ON COLUMN lhyy.followup_package_import_state.schema_diff_json IS '包内结构差异和处理建议';

CREATE INDEX IF NOT EXISTS ix_followup_package_import_state_status
    ON lhyy.followup_package_import_state (hospital_code, import_status, sequence_no);

CREATE INDEX IF NOT EXISTS ix_followup_package_import_state_schema_review
    ON lhyy.followup_package_import_state (requires_schema_review, schema_diff_level)
    WHERE requires_schema_review = true;

CREATE TABLE IF NOT EXISTS lhyy.followup_package_schema_check (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    hospital_code text NOT NULL,
    package_id text NOT NULL,
    check_status text NOT NULL,
    export_contract_version text,
    importer_version text,
    schema_diff_level text,
    compatible boolean NOT NULL DEFAULT false,
    check_result_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    restore_plan_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    decision_status text NOT NULL DEFAULT 'None',
    decision_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    checked_at timestamp without time zone NOT NULL DEFAULT now(),
    decided_at timestamp without time zone,
    operator_name text,
    CONSTRAINT uq_followup_package_schema_check_package UNIQUE (hospital_code, package_id)
);

COMMENT ON TABLE lhyy.followup_package_schema_check IS 'FollowUp 数据包结构校验、结构差异和人工处理决策。';
COMMENT ON COLUMN lhyy.followup_package_schema_check.restore_plan_json IS '根据结构差异生成的恢复处理方案';
COMMENT ON COLUMN lhyy.followup_package_schema_check.decision_json IS '人工确认的结构差异处理决策';

CREATE INDEX IF NOT EXISTS ix_followup_package_schema_check_status
    ON lhyy.followup_package_schema_check (check_status, schema_diff_level, checked_at DESC);

CREATE TABLE IF NOT EXISTS lhyy.followup_package_backup_record (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    hospital_code text NOT NULL,
    package_id text NOT NULL,
    backup_type text NOT NULL,
    backup_status text NOT NULL DEFAULT 'Created',
    database_backup_path text,
    attachment_backup_path text,
    backup_hash text,
    backup_size_bytes bigint NOT NULL DEFAULT 0,
    detail_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamp without time zone NOT NULL DEFAULT now(),
    finished_at timestamp without time zone,
    operator_name text
);

COMMENT ON TABLE lhyy.followup_package_backup_record IS 'FollowUp 数据包导入前数据库和附件备份记录。';

CREATE INDEX IF NOT EXISTS ix_followup_package_backup_record_package
    ON lhyy.followup_package_backup_record (hospital_code, package_id, created_at DESC);

CREATE TABLE IF NOT EXISTS lhyy.followup_package_restore_record (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    hospital_code text NOT NULL,
    package_id text NOT NULL,
    restore_mode text NOT NULL,
    restore_status text NOT NULL DEFAULT 'Pending',
    backup_record_id uuid,
    restore_plan_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    result_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    error_code text,
    error_message text,
    requested_by text,
    requested_at timestamp without time zone NOT NULL DEFAULT now(),
    started_at timestamp without time zone,
    finished_at timestamp without time zone
);

COMMENT ON TABLE lhyy.followup_package_restore_record IS 'FollowUp 数据包恢复操作记录，页面二次确认后写入。';

CREATE INDEX IF NOT EXISTS ix_followup_package_restore_record_package
    ON lhyy.followup_package_restore_record (hospital_code, package_id, requested_at DESC);

CREATE TABLE IF NOT EXISTS lhyy.followup_package_import_log (
    id bigserial PRIMARY KEY,
    hospital_code text,
    package_id text,
    operation text NOT NULL,
    level text NOT NULL DEFAULT 'Info',
    message text NOT NULL DEFAULT '',
    detail_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamp without time zone NOT NULL DEFAULT now()
);

COMMENT ON TABLE lhyy.followup_package_import_log IS 'FollowUp 数据包校验、导入、恢复日志。禁止记录患者明细、token 明文和私钥。';

CREATE INDEX IF NOT EXISTS ix_followup_package_import_log_hospital_created
    ON lhyy.followup_package_import_log (hospital_code, created_at DESC);

CREATE INDEX IF NOT EXISTS ix_followup_package_import_log_package
    ON lhyy.followup_package_import_log (package_id, created_at DESC);

-- 外键约束：仅约束本次新增表之间的强一致关系。
-- 日志表保留弱关联，避免失败日志因导入状态记录尚未创建而写入失败。
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_followup_package_import_state_previous'
    ) THEN
        ALTER TABLE lhyy.followup_package_import_state
            ADD CONSTRAINT fk_followup_package_import_state_previous
            FOREIGN KEY (hospital_code, previous_package_id)
            REFERENCES lhyy.followup_package_import_state (hospital_code, package_id)
            ON UPDATE CASCADE
            ON DELETE RESTRICT;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_followup_package_schema_check_import_state'
    ) THEN
        ALTER TABLE lhyy.followup_package_schema_check
            ADD CONSTRAINT fk_followup_package_schema_check_import_state
            FOREIGN KEY (hospital_code, package_id)
            REFERENCES lhyy.followup_package_import_state (hospital_code, package_id)
            ON UPDATE CASCADE
            ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_followup_package_backup_record_import_state'
    ) THEN
        ALTER TABLE lhyy.followup_package_backup_record
            ADD CONSTRAINT fk_followup_package_backup_record_import_state
            FOREIGN KEY (hospital_code, package_id)
            REFERENCES lhyy.followup_package_import_state (hospital_code, package_id)
            ON UPDATE CASCADE
            ON DELETE RESTRICT;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_followup_package_restore_record_import_state'
    ) THEN
        ALTER TABLE lhyy.followup_package_restore_record
            ADD CONSTRAINT fk_followup_package_restore_record_import_state
            FOREIGN KEY (hospital_code, package_id)
            REFERENCES lhyy.followup_package_import_state (hospital_code, package_id)
            ON UPDATE CASCADE
            ON DELETE RESTRICT;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_followup_package_restore_record_backup_record'
    ) THEN
        ALTER TABLE lhyy.followup_package_restore_record
            ADD CONSTRAINT fk_followup_package_restore_record_backup_record
            FOREIGN KEY (backup_record_id)
            REFERENCES lhyy.followup_package_backup_record (id)
            ON UPDATE CASCADE
            ON DELETE RESTRICT;
    END IF;
END $$;

