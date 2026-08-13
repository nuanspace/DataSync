BEGIN;

CREATE TABLE IF NOT EXISTS lhyy.esb_html_profile (
    id                       SERIAL PRIMARY KEY,
    tran_code                VARCHAR(20) NOT NULL,
    integration_project_code VARCHAR(50),
    profile_name             VARCHAR(100),
    is_enabled               BOOLEAN NOT NULL DEFAULT TRUE,
    source_path              VARCHAR(500) NOT NULL DEFAULT '$main.FILE_CONTENT',
    max_input_bytes          BIGINT NOT NULL DEFAULT 5242880,
    preserve_sections        BOOLEAN NOT NULL DEFAULT TRUE,
    section_headings         TEXT NOT NULL DEFAULT '',
    extraction_rules         JSONB NOT NULL DEFAULT '[]'::JSONB,
    description              VARCHAR(500),
    created_at               TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at               TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_esb_html_profile_tran_code
    ON lhyy.esb_html_profile (tran_code);

CREATE INDEX IF NOT EXISTS ix_esb_html_profile_project_tran
    ON lhyy.esb_html_profile (integration_project_code, tran_code);

COMMENT ON TABLE lhyy.esb_html_profile
    IS 'Base64 HTML 医疗文书的确定性章节及字段提取配置';
COMMENT ON COLUMN lhyy.esb_html_profile.source_path
    IS '消息中 Base64 文本的 JSON 路径';
COMMENT ON COLUMN lhyy.esb_html_profile.extraction_rules
    IS '确定性字段提取规则，不调用大模型';

COMMIT;
