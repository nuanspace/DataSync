-- 增加过滤规则组号，支持同组 AND、组间 OR

ALTER TABLE IF EXISTS lhyy.esb_filter_rule
    ADD COLUMN IF NOT EXISTS rule_group INT NOT NULL DEFAULT 1;

COMMENT ON COLUMN lhyy.esb_filter_rule.rule_group IS '规则组号：同组 AND，组间 OR';

CREATE INDEX IF NOT EXISTS ix_esb_filter_rule_tc_group
    ON lhyy.esb_filter_rule (tran_code, rule_group);

-- 消息日志查询：支持按病案号 + 事件开始时间过滤
ALTER TABLE IF EXISTS lhyy.esb_messages
    ADD COLUMN IF NOT EXISTS mrn VARCHAR(100),
    ADD COLUMN IF NOT EXISTS resolved_event_time TIMESTAMP;

CREATE INDEX IF NOT EXISTS ix_esb_messages_mrn_event_time
    ON lhyy.esb_messages (mrn, resolved_event_time);
