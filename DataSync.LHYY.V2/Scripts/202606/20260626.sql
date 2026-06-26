BEGIN;

CREATE TABLE IF NOT EXISTS lhyy.esb_ocr_profile (
    id                       SERIAL PRIMARY KEY,
    tran_code                VARCHAR(20) NOT NULL,
    integration_project_code VARCHAR(50),
    profile_name             VARCHAR(100),
    is_enabled               BOOLEAN NOT NULL DEFAULT TRUE,
    source_kind              INTEGER NOT NULL DEFAULT 0,
    source_path              VARCHAR(500) NOT NULL DEFAULT '$.pdfPath',
    language                 VARCHAR(50) NOT NULL DEFAULT 'chi_sim',
    dpi                      INTEGER NOT NULL DEFAULT 300,
    page_seg_mode            INTEGER NOT NULL DEFAULT 11,
    max_pages                INTEGER,
    max_input_bytes          BIGINT,
    timeout_seconds          INTEGER NOT NULL DEFAULT 120,
    keep_work_files          BOOLEAN NOT NULL DEFAULT FALSE,
    allowed_file_roots       TEXT,
    output_json_path         VARCHAR(1000),
    description              VARCHAR(500),
    created_at               TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at               TIMESTAMP NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE lhyy.esb_ocr_profile
    IS 'ESB 接口 OCR 转换配置，启用后由 OcrMessageProcessor 将 PDF 转换结果挂载到消息 Ocr 节点';

COMMENT ON COLUMN lhyy.esb_ocr_profile.source_kind
    IS 'PDF 来源类型：0=FilePath，1=Url，2=Base64';

COMMENT ON COLUMN lhyy.esb_ocr_profile.source_path
    IS '从原始消息中提取 PDF 来源值的 JSONPath，例如 $.pdfPath、$.pdfUrl、$.pdfBase64';

COMMENT ON COLUMN lhyy.esb_ocr_profile.allowed_file_roots
    IS '允许读取的文件根目录，多个目录使用分号或换行分隔；FilePath 来源必须配置';

CREATE INDEX IF NOT EXISTS ix_esb_ocr_profile_tran_code
    ON lhyy.esb_ocr_profile (tran_code);

CREATE INDEX IF NOT EXISTS ix_esb_ocr_profile_project_tran
    ON lhyy.esb_ocr_profile (integration_project_code, tran_code);

COMMIT;
