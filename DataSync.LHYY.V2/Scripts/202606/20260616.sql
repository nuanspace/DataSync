BEGIN;

ALTER TABLE IF EXISTS lhyy.esb_interface_config
    ADD COLUMN IF NOT EXISTS medical_record_sync_role INTEGER NOT NULL DEFAULT 0;

COMMENT ON COLUMN lhyy.esb_interface_config.medical_record_sync_role
    IS 'Active 病历补采角色：0=None，1=CaseDriver，2=Supplemental';

CREATE TABLE IF NOT EXISTS lhyy.active_medical_records (
    id                       BIGSERIAL PRIMARY KEY,
    integration_project_code VARCHAR(50),
    tran_code                VARCHAR(20),
    mrn                      VARCHAR(100) NOT NULL,
    inpatient_no             VARCHAR(100),
    visit_no                 VARCHAR(100),
    patient_id               UUID NOT NULL,
    event_id                 UUID NOT NULL,
    event_type_name          VARCHAR(100) NOT NULL DEFAULT '',
    admission_time           TIMESTAMP,
    discharge_time           TIMESTAMP,
    status                   VARCHAR(20) NOT NULL DEFAULT 'Active',
    created_at               TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at               TIMESTAMP NOT NULL DEFAULT NOW(),
    finished_at              TIMESTAMP
);

COMMENT ON TABLE lhyy.active_medical_records
    IS 'LHYY 侧 Active 病历清单，供 CYYY 在院病历补采拉取';

CREATE INDEX IF NOT EXISTS ix_active_medical_records_status
    ON lhyy.active_medical_records (status);

CREATE INDEX IF NOT EXISTS ix_active_medical_records_project
    ON lhyy.active_medical_records (integration_project_code);

CREATE INDEX IF NOT EXISTS ix_active_medical_records_project_inpatient
    ON lhyy.active_medical_records (integration_project_code, inpatient_no);

CREATE INDEX IF NOT EXISTS ix_active_medical_records_project_identity
    ON lhyy.active_medical_records (integration_project_code, mrn, inpatient_no, visit_no);

UPDATE lhyy.esb_interface_config
SET medical_record_sync_role = 1
WHERE integration_project_code = 'LHYY'
  AND upper(tran_code) IN ('V_BLOOD_VESSEL_RYXX', 'V_BLOOD_VESSEL_CYXX');

UPDATE lhyy.esb_interface_config
SET medical_record_sync_role = 2
WHERE integration_project_code = 'LHYY'
  AND upper(tran_code) IN ('V_BLOOD_VESSEL_HYSJ', 'V_BLOOD_VESSEL_JCXX');

DELETE FROM lhyy.esb_filter_rule
WHERE integration_project_code = 'LHYY'
  AND mapping_id IS NULL
  AND upper(tran_code) IN ('V_BLOOD_VESSEL_RYXX', 'V_BLOOD_VESSEL_CYXX')
  AND description LIKE '血管项目病种过滤%';

WITH rules(tran_code, source_path, keyword, rule_group, sort_order, description) AS (
    VALUES
        ('V_BLOOD_VESSEL_RYXX', 'WM_INITAL_DIAGNOSIS_NAME', '下肢静脉功能不全', 101, 1, '血管项目病种过滤-入院诊断'),
        ('V_BLOOD_VESSEL_RYXX', 'WM_INITAL_DIAGNOSIS_NAME', '大隐静脉曲张', 102, 1, '血管项目病种过滤-入院诊断'),
        ('V_BLOOD_VESSEL_RYXX', 'WM_INITAL_DIAGNOSIS_NAME', '髂静脉压迫', 103, 1, '血管项目病种过滤-入院诊断'),
        ('V_BLOOD_VESSEL_RYXX', 'WM_INITAL_DIAGNOSIS_NAME', '深静脉血栓形成', 104, 1, '血管项目病种过滤-入院诊断'),
        ('V_BLOOD_VESSEL_CYXX', 'CYZD', '下肢静脉功能不全', 101, 1, '血管项目病种过滤-出院诊断'),
        ('V_BLOOD_VESSEL_CYXX', 'CYZD', '大隐静脉曲张', 102, 1, '血管项目病种过滤-出院诊断'),
        ('V_BLOOD_VESSEL_CYXX', 'CYZD', '髂静脉压迫', 103, 1, '血管项目病种过滤-出院诊断'),
        ('V_BLOOD_VESSEL_CYXX', 'CYZD', '深静脉血栓形成', 104, 1, '血管项目病种过滤-出院诊断')
)
INSERT INTO lhyy.esb_filter_rule (
    tran_code,
    integration_project_code,
    source_path,
    operator,
    compare_value,
    rule_group,
    mapping_id,
    filter_scope,
    is_enabled,
    sort_order,
    description
)
SELECT
    tran_code,
    'LHYY',
    source_path,
    'contains',
    keyword,
    rule_group,
    NULL,
    0,
    TRUE,
    sort_order,
    description
FROM rules;

COMMIT;
