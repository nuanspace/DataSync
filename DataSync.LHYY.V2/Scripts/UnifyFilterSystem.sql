-- 统一过滤系统迁移脚本
-- 1. 修改 esb_filter_rule 表：删除旧列、添加新列
-- 2. 数据迁移：旧规则转换为新格式
-- 3. EsbMappingFilter → EsbFilterRule 数据迁移
-- 4. 删除 esb_mapping_filter 表

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'lhyy'
          AND table_name = 'esb_filter_rule'
          AND column_name = 'filter_level'
    ) AND EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'lhyy'
          AND table_name = 'esb_filter_rule'
          AND column_name = 'array_path'
    ) THEN
        UPDATE lhyy.esb_filter_rule
        SET source_path = CONCAT(array_path, '[].', source_path)
        WHERE filter_level = 1 AND array_path IS NOT NULL AND array_path != '';
    END IF;
END $$;

-- ============================================================
-- 步骤 2：添加新列 filter_scope
-- ============================================================
ALTER TABLE lhyy.esb_filter_rule ADD COLUMN IF NOT EXISTS filter_scope smallint NOT NULL DEFAULT 0;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'lhyy'
          AND table_name = 'esb_filter_rule'
          AND column_name = 'filter_level'
    ) THEN
        UPDATE lhyy.esb_filter_rule SET filter_scope = 1 WHERE filter_level = 1;
        UPDATE lhyy.esb_filter_rule SET filter_scope = 0 WHERE filter_level IN (0, 2);
    END IF;
END $$;

-- ============================================================
-- 步骤 3：删除旧列
-- ============================================================
ALTER TABLE lhyy.esb_filter_rule DROP COLUMN IF EXISTS filter_level;
ALTER TABLE lhyy.esb_filter_rule DROP COLUMN IF EXISTS card_id;
ALTER TABLE lhyy.esb_filter_rule DROP COLUMN IF EXISTS array_path;

-- ============================================================
-- 步骤 4：EsbMappingFilter → EsbFilterRule 数据迁移
-- accept → in 运算符，reject → not_in 运算符
-- ValueList JSON 数组 → 逗号分隔字符串
-- ============================================================
DO $$
BEGIN
    IF to_regclass('lhyy.esb_mapping_filter') IS NOT NULL THEN
        INSERT INTO lhyy.esb_filter_rule (tran_code, source_path, operator, compare_value, mapping_id, filter_scope, is_enabled, sort_order, description)
        SELECT
            fm.tran_code,
            mf.source_path,
            CASE mf.filter_type WHEN 'accept' THEN 'in' WHEN 'reject' THEN 'not_in' ELSE 'in' END,
            ARRAY_TO_STRING(ARRAY(SELECT jsonb_array_elements_text(mf.value_list::jsonb)), ','),
            mf.mapping_id,
            0,
            mf.is_enabled,
            mf.sort_order,
            mf.description
        FROM lhyy.esb_mapping_filter mf
        JOIN lhyy.esb_field_mapping fm ON fm.id = mf.mapping_id;
    END IF;
END $$;

-- ============================================================
-- 步骤 5：删除 esb_mapping_filter 表
-- ============================================================
DROP TABLE IF EXISTS lhyy.esb_mapping_filter;

-- ============================================================
-- 步骤 6：更新索引
-- ============================================================
DROP INDEX IF EXISTS lhyy."IX_esb_filter_rule_tran_code_filter_level";
CREATE INDEX IF NOT EXISTS "IX_esb_filter_rule_tran_code" ON lhyy.esb_filter_rule (tran_code);
CREATE INDEX IF NOT EXISTS "IX_esb_filter_rule_mapping_id" ON lhyy.esb_filter_rule (mapping_id);
