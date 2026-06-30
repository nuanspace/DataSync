BEGIN;

CREATE TABLE IF NOT EXISTS lhyy.esb_dict_template (
    id                 SERIAL PRIMARY KEY,
    template_code      VARCHAR(100) NOT NULL,
    template_name      VARCHAR(100) NOT NULL,
    category           VARCHAR(50) NOT NULL DEFAULT '',
    default_dict_code  VARCHAR(100),
    default_match_mode VARCHAR(50) NOT NULL DEFAULT 'contains',
    description        VARCHAR(500),
    is_enabled         BOOLEAN NOT NULL DEFAULT TRUE,
    sort_order         INTEGER NOT NULL DEFAULT 0,
    created_at         TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at         TIMESTAMP NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE lhyy.esb_dict_template IS '字典模板';
COMMENT ON COLUMN lhyy.esb_dict_template.template_code IS '模板代码';
COMMENT ON COLUMN lhyy.esb_dict_template.template_name IS '模板名称';
COMMENT ON COLUMN lhyy.esb_dict_template.category IS '模板分类';
COMMENT ON COLUMN lhyy.esb_dict_template.default_dict_code IS '推荐字典代码';
COMMENT ON COLUMN lhyy.esb_dict_template.default_match_mode IS '推荐字典匹配模式';

CREATE UNIQUE INDEX IF NOT EXISTS ux_esb_dict_template_code
    ON lhyy.esb_dict_template (template_code);

CREATE INDEX IF NOT EXISTS ix_esb_dict_template_category
    ON lhyy.esb_dict_template (category);

CREATE TABLE IF NOT EXISTS lhyy.esb_dict_template_item (
    id           SERIAL PRIMARY KEY,
    template_id  INTEGER NOT NULL REFERENCES lhyy.esb_dict_template(id) ON DELETE CASCADE,
    source_value VARCHAR(200) NOT NULL,
    target_value VARCHAR(200) NOT NULL,
    sort_order   INTEGER NOT NULL DEFAULT 0,
    description  VARCHAR(500),
    is_enabled   BOOLEAN NOT NULL DEFAULT TRUE
);

COMMENT ON TABLE lhyy.esb_dict_template_item IS '字典模板条目';
COMMENT ON COLUMN lhyy.esb_dict_template_item.source_value IS '命中条件，格式与 esb_dict.source_value 一致';
COMMENT ON COLUMN lhyy.esb_dict_template_item.target_value IS '目标值';

CREATE INDEX IF NOT EXISTS ix_esb_dict_template_item_template_sort
    ON lhyy.esb_dict_template_item (template_id, sort_order);

INSERT INTO lhyy.esb_dict_template (
    template_code,
    template_name,
    category,
    default_dict_code,
    default_match_mode,
    description,
    is_enabled,
    sort_order,
    updated_at
)
VALUES (
    'smoking_status',
    '吸烟情况',
    '生活史',
    'smoking_status',
    'priority_first',
    '用于将病历中的吸烟描述转换为项目常见吸烟状态选项。只写“已戒烟”或“戒烟”但没有时间时，不自动归类。',
    TRUE,
    10,
    NOW()
)
ON CONFLICT (template_code) DO UPDATE SET
    template_name = EXCLUDED.template_name,
    category = EXCLUDED.category,
    default_dict_code = EXCLUDED.default_dict_code,
    default_match_mode = EXCLUDED.default_match_mode,
    description = EXCLUDED.description,
    is_enabled = EXCLUDED.is_enabled,
    sort_order = EXCLUDED.sort_order,
    updated_at = NOW();

DELETE FROM lhyy.esb_dict_template_item
WHERE template_id = (
    SELECT id
    FROM lhyy.esb_dict_template
    WHERE template_code = 'smoking_status'
);

INSERT INTO lhyy.esb_dict_template_item (
    template_id,
    source_value,
    target_value,
    sort_order,
    description,
    is_enabled
)
SELECT
    t.id,
    v.source_value,
    v.target_value,
    v.sort_order,
    v.description,
    TRUE
FROM lhyy.esb_dict_template t
CROSS JOIN (
    VALUES
        ('regex:戒烟.{0,8}(?:[1-9]|[1-8]\d|90)\s*(?:天|日)', '戒烟<=3月', 10, '戒烟 1 到 90 天，例如“戒烟30天”“已戒烟 60 日”。'),
        ('regex:戒烟.{0,8}(?:1|2|3|一|二|两|三)\s*个?月', '戒烟<=3月', 20, '戒烟 1 到 3 个月，例如“戒烟2个月”“已戒烟三月”。'),
        ('戒烟不足3月', '戒烟<=3月', 30, '明确写“戒烟不足3月”。'),
        ('戒烟不到3月', '戒烟<=3月', 40, '明确写“戒烟不到3月”。'),
        ('戒烟未满3月', '戒烟<=3月', 50, '明确写“戒烟未满3月”。'),
        ('戒烟小于3月', '戒烟<=3月', 60, '明确写“戒烟小于3月”。'),
        ('戒烟小于等于3月', '戒烟<=3月', 70, '明确写“戒烟小于等于3月”。'),

        ('regex:戒烟.{0,8}(?:[4-9]|1[0-2]|四|五|六|七|八|九|十|十一|十二)\s*个?月', '戒烟>=3月', 80, '戒烟 4 到 12 个月，例如“戒烟4个月”“戒烟十月”。'),
        ('regex:戒烟.{0,8}(?:半年|[1-9]\d*\s*年|[一二三四五六七八九十]+年|多年)', '戒烟>=3月', 90, '戒烟半年、一年、多年等，例如“戒烟半年”“已戒烟2年”。'),
        ('戒烟超过3月', '戒烟>=3月', 100, '明确写“戒烟超过3月”。'),
        ('戒烟大于3月', '戒烟>=3月', 110, '明确写“戒烟大于3月”。'),
        ('戒烟大于等于3月', '戒烟>=3月', 120, '明确写“戒烟大于等于3月”。'),
        ('戒烟≥3月', '戒烟>=3月', 130, '明确写“戒烟≥3月”。'),
        ('戒烟>=3月', '戒烟>=3月', 140, '明确写“戒烟>=3月”。'),
        ('长期戒烟', '戒烟>=3月', 150, '明确写“长期戒烟”。'),

        ('从不吸烟', '从不吸烟', 160, '明确写“从不吸烟”。'),
        ('否认吸烟', '从不吸烟', 170, '否认当前或既往吸烟。'),
        ('否认烟草', '从不吸烟', 180, '否认烟草接触。'),
        ('无吸烟史', '从不吸烟', 190, '明确写无吸烟史。'),
        ('无烟史', '从不吸烟', 200, '明确写无烟史。'),
        ('未吸烟', '从不吸烟', 210, '明确写未吸烟。'),
        ('平素不吸烟', '从不吸烟', 220, '平素不吸烟。'),
        ('不吸烟', '从不吸烟', 230, '普通“不吸烟”描述。若院内常用“目前不吸烟”表示已戒烟，建议不要配置这一条。'),

        ('偶尔吸烟', '偶尔吸烟', 240, '明确写偶尔吸烟。'),
        ('偶吸', '偶尔吸烟', 250, '简写“偶吸”。'),
        ('偶有吸烟', '偶尔吸烟', 260, '明确写偶有吸烟。'),
        ('间断吸烟', '偶尔吸烟', 270, '间断吸烟。'),
        ('少量吸烟', '偶尔吸烟', 280, '少量吸烟。'),
        ('社交性吸烟', '偶尔吸烟', 290, '社交性吸烟。'),

        ('仍在吸烟', '仍在吸烟', 300, '明确写仍在吸烟。'),
        ('现吸烟', '仍在吸烟', 310, '明确写现吸烟。'),
        ('目前吸烟', '仍在吸烟', 320, '明确写目前吸烟。'),
        ('仍吸烟', '仍在吸烟', 330, '明确写仍吸烟。'),
        ('继续吸烟', '仍在吸烟', 340, '明确写继续吸烟。'),
        ('长期吸烟', '仍在吸烟', 350, '长期吸烟，且未提戒烟。'),
        ('regex:^(?!.*(?:戒烟|已戒)).*(?:每日|每天|日均).{0,8}\d+\s*支', '仍在吸烟', 360, '未出现戒烟/已戒时，描述每日多少支，例如“每日10支”。'),
        ('regex:^(?!.*(?:戒烟|已戒)).*\d+\s*支\s*/?\s*(?:日|天|每天|每日)', '仍在吸烟', 370, '未出现戒烟/已戒时，描述“10支/日”“10支每天”。'),
        ('regex:^(?!.*(?:戒烟|已戒)).*(?:吸烟|抽烟).{0,12}\d+\s*(?:年|月|支|包)', '仍在吸烟', 380, '未出现戒烟/已戒时，描述吸烟年限、月数、支数、包数。'),
        ('吸烟 && 包', '仍在吸烟', 390, '同一文本同时包含“吸烟”和“包”。'),
        ('抽烟 && 支', '仍在吸烟', 400, '同一文本同时包含“抽烟”和“支”。')
) AS v(source_value, target_value, sort_order, description)
WHERE t.template_code = 'smoking_status';

COMMIT;
