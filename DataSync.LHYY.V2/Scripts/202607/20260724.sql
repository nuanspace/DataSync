CREATE INDEX IF NOT EXISTS ix_active_medical_records_project_event
    ON lhyy.active_medical_records (integration_project_code, event_id);

-- 从历史消息中补齐住院身份的住院次数，不重新执行消息处理和业务写入。
WITH ranked_candidates AS (
    SELECT
        identity.id AS identity_id,
        BTRIM(message.visit_no) AS visit_no,
        ROW_NUMBER() OVER (
            PARTITION BY identity.id
            ORDER BY
                CASE
                    WHEN message.event_id = identity.event_id THEN 0
                    WHEN message.mrn = identity.mrn
                        AND NULLIF(BTRIM(message.inpatient_no), '') = NULLIF(BTRIM(identity.inpatient_no), '') THEN 1
                    WHEN NULLIF(BTRIM(message.inpatient_no), '') = NULLIF(BTRIM(identity.inpatient_no), '') THEN 2
                    ELSE 3
                END,
                COALESCE(message.processed_at, message.created_at) DESC,
                message.id DESC
        ) AS row_number
    FROM lhyy.esb_event_identity AS identity
    INNER JOIN lhyy.esb_messages AS message
        ON message.integration_project_code IS NOT DISTINCT FROM identity.integration_project_code
        AND (
            (identity.event_id <> '00000000-0000-0000-0000-000000000000'::uuid
                AND message.event_id = identity.event_id)
            OR (NULLIF(BTRIM(identity.inpatient_no), '') IS NOT NULL
                AND NULLIF(BTRIM(message.inpatient_no), '') = NULLIF(BTRIM(identity.inpatient_no), ''))
            OR (message.mrn = identity.mrn
                AND message.resolved_event_time IS NOT NULL
                AND identity.event_start_time IS NOT NULL
                AND message.resolved_event_time::date = identity.event_start_time::date)
        )
    WHERE NULLIF(BTRIM(identity.visit_no), '') IS NULL
        AND NULLIF(BTRIM(message.visit_no), '') IS NOT NULL
)
UPDATE lhyy.esb_event_identity AS identity
SET visit_no = candidate.visit_no,
    updated_at = NOW()
FROM ranked_candidates AS candidate
WHERE candidate.identity_id = identity.id
    AND candidate.row_number = 1
    AND NULLIF(BTRIM(identity.visit_no), '') IS NULL;
