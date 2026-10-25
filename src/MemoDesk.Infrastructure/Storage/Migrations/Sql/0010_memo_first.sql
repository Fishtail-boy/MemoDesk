-- v10：备忘优先的数据基础。
-- 1) 原文解析操作版本：人工重试推进版本，晚到结果不能覆盖新状态。
-- 2) 事项人工修订标记：后台 AI 结果不得覆盖用户编辑、完成、归档、删除或改提醒。
-- 3) 解析结果可处于“已保存未应用”，崩溃重启后先应用已有结果而不是重新调用 AI。
-- 4) 每条备忘有独立的录入事件，供工作记录按事件时间回看。
-- 5) 旧数据迁移按原文分组记录完成状态，支持重复运行。

ALTER TABLE captures ADD COLUMN parse_operation_version INTEGER NOT NULL DEFAULT 1;

ALTER TABLE items ADD COLUMN human_revised INTEGER NOT NULL DEFAULT 0;

ALTER TABLE parse_drafts ADD COLUMN operation_version INTEGER NOT NULL DEFAULT 1;
ALTER TABLE parse_drafts ADD COLUMN applied_at_utc TEXT NULL;
ALTER TABLE parse_drafts ADD COLUMN applied_memo_id TEXT NULL;

ALTER TABLE timeline_events ADD COLUMN capture_id TEXT NULL REFERENCES captures(id);

CREATE INDEX ix_timeline_type_occurred ON timeline_events (event_type, occurred_at_utc);
CREATE INDEX ix_timeline_capture ON timeline_events (capture_id);

-- 一条备忘最多一条“录入备忘”事件；服务重复提交/重启恢复不会重复留痕。
CREATE UNIQUE INDEX ux_timeline_memo_captured
    ON timeline_events (item_id)
    WHERE event_type = 'MemoCaptured';

CREATE TABLE migration_context (
    capture_id     TEXT NOT NULL PRIMARY KEY REFERENCES captures(id) ON DELETE CASCADE,
    state          TEXT NOT NULL,
    error          TEXT NULL,
    applied_at_utc TEXT NULL
);

CREATE INDEX ix_migration_context_state ON migration_context (state);

-- 后台整理结果独立于旧“待确认草稿”：结果可先落盘，再在独立事务中应用。
CREATE TABLE memo_parse_results (
    id                TEXT NOT NULL PRIMARY KEY,
    capture_id        TEXT NOT NULL REFERENCES captures(id),
    operation_version INTEGER NOT NULL,
    version           INTEGER NOT NULL,
    status            TEXT NOT NULL,
    candidates_json   TEXT NOT NULL DEFAULT '[]',
    raw_ai_json       TEXT NULL,
    model             TEXT NULL,
    prompt_version    TEXT NULL,
    schema_version    TEXT NULL,
    attempt_count     INTEGER NOT NULL DEFAULT 0,
    last_error        TEXT NULL,
    applied_at_utc    TEXT NULL,
    applied_memo_id   TEXT NULL,
    created_at_utc    TEXT NOT NULL,
    updated_at_utc    TEXT NOT NULL
);

CREATE UNIQUE INDEX ux_memo_parse_capture_version ON memo_parse_results (capture_id, version);
CREATE INDEX ix_memo_parse_status ON memo_parse_results (status, created_at_utc);
