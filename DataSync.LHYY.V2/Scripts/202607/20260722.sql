-- 本文件遵循 LHYY SQL 归档规则，但目标结构属于 ConnectionStrings:CubeDb。
-- 在 DataSyncDb 常规升级链中执行时会安全跳过；部署时还必须手工连接 CubeDb 再执行同一文件。
-- 用途：DataSync 在把回传患者 source_type 适配为 care 后，保留可靠的原始来源标识。

DO $migration$
BEGIN
    IF to_regclass('public.patient') IS NULL
       OR to_regclass('care.patient_event') IS NULL
       OR to_regclass('form.form_project') IS NULL THEN
        RAISE NOTICE '跳过 FollowUp CubeDb 来源映射迁移：当前数据库不是预期的 CubeDb。';
        RETURN;
    END IF;

    CREATE SCHEMA IF NOT EXISTS datasync;

    CREATE TABLE IF NOT EXISTS datasync.followup_patient_source_map (
        patient_id uuid PRIMARY KEY,
        original_source_type text,
        hospital_code text NOT NULL,
        first_package_id text NOT NULL,
        last_package_id text NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
        updated_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
    );

    ALTER TABLE datasync.followup_patient_source_map
        ADD COLUMN IF NOT EXISTS original_source_type text;

    COMMENT ON TABLE datasync.followup_patient_source_map
        IS 'DataSync 医院回传患者来源标识；患者写入 public.patient 时 source_type 统一适配为 care。';
    COMMENT ON COLUMN datasync.followup_patient_source_map.patient_id
        IS '与 CubeDb public.patient.id 保持相同 UUID。';
    COMMENT ON COLUMN datasync.followup_patient_source_map.original_source_type
        IS '回传包中该患者最近一次携带的原始 source_type，可为空。';

    CREATE INDEX IF NOT EXISTS ix_followup_patient_source_map_hospital
        ON datasync.followup_patient_source_map (hospital_code, patient_id);
END
$migration$;
