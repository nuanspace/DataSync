ALTER TABLE lhyy.esb_field_mapping
    ADD COLUMN IF NOT EXISTS dict_match_mode VARCHAR(50) NOT NULL DEFAULT 'contains';

COMMENT ON COLUMN lhyy.esb_field_mapping.dict_match_mode IS '字典匹配模式：contains=普通包含，contains_exclude_negation=包含并排除否定';
