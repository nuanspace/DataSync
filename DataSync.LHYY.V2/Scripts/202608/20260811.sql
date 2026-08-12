-- FollowUp 患者身份映射属于 DataSync 管理状态，只允许在 ConnectionStrings:DataSyncDb 执行。
-- CubeDb 只承载 NTCare 既有业务表，本脚本不对 CubeDb 创建或修改任何结构。

DO $migration$
BEGIN
    IF to_regclass('lhyy.followup_package_import_state') IS NULL THEN
        RAISE NOTICE '跳过 FollowUp 患者身份映射迁移：当前数据库不是已初始化的 DataSyncDb。';
        RETURN;
    END IF;

    CREATE TABLE IF NOT EXISTS lhyy.followup_patient_identity_map (
        hospital_code text NOT NULL,
        source_patient_id uuid NOT NULL,
        target_patient_id uuid NOT NULL,
        source_unique_patient_id uuid,
        target_unique_patient_id uuid,
        identity_match_basis text NOT NULL DEFAULT 'Id',
        original_source_type text,
        first_package_id text NOT NULL,
        last_package_id text NOT NULL,
        created_at timestamp without time zone NOT NULL DEFAULT now(),
        updated_at timestamp without time zone NOT NULL DEFAULT now(),
        CONSTRAINT pk_followup_patient_identity_map
            PRIMARY KEY (hospital_code, source_patient_id),
        CONSTRAINT uq_followup_patient_identity_map_target
            UNIQUE (hospital_code, target_patient_id),
        CONSTRAINT ck_followup_patient_identity_map_match_basis
            CHECK (identity_match_basis IN ('Id', 'SidNumber', 'Demographics'))
    );

    CREATE INDEX IF NOT EXISTS ix_followup_patient_identity_map_source_unique
        ON lhyy.followup_patient_identity_map (hospital_code, source_unique_patient_id)
        WHERE source_unique_patient_id IS NOT NULL;

    CREATE INDEX IF NOT EXISTS ix_followup_patient_identity_map_target_unique
        ON lhyy.followup_patient_identity_map (hospital_code, target_unique_patient_id)
        WHERE target_unique_patient_id IS NOT NULL;

    COMMENT ON TABLE lhyy.followup_patient_identity_map
        IS 'FollowUp 医院数据包专用患者身份映射；普通 ESB 和本院其他 DataSync 导入不得读写。';
    COMMENT ON COLUMN lhyy.followup_patient_identity_map.source_patient_id
        IS 'FollowUp 云端 public.patient.id。';
    COMMENT ON COLUMN lhyy.followup_patient_identity_map.target_patient_id
        IS '院端 CubeDb 实际复用或新增的 public.patient.id。';
    COMMENT ON COLUMN lhyy.followup_patient_identity_map.identity_match_basis
        IS '唯一患者匹配依据：Id、SidNumber 或 Demographics。';
END
$migration$;
