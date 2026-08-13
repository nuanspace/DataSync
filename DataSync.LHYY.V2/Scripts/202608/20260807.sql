BEGIN;

ALTER TABLE IF EXISTS lhyy.esb_ocr_profile
    ADD COLUMN IF NOT EXISTS extraction_rules JSONB NOT NULL DEFAULT '[]'::JSONB;

COMMENT ON COLUMN lhyy.esb_ocr_profile.extraction_rules
    IS 'OCR 字段提取规则；每条规则包含字段代码、名称、提取方式、页码和正则表达式';

COMMIT;
