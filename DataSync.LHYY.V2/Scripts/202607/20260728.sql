ALTER TABLE IF EXISTS lhyy.esb_interface_config
    ADD COLUMN IF NOT EXISTS combined_visit_identity_source_path VARCHAR(500);

ALTER TABLE IF EXISTS lhyy.esb_interface_config
    ADD COLUMN IF NOT EXISTS combined_visit_identity_format SMALLINT NOT NULL DEFAULT 0;

COMMENT ON COLUMN lhyy.esb_interface_config.combined_visit_identity_source_path IS '病案号与住院次数组合标识在消息中的路径';
COMMENT ON COLUMN lhyy.esb_interface_config.combined_visit_identity_format IS '组合格式：0=未使用，1=病案号_住院次数，2=病案号住院次数';
