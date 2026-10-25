-- 解析草稿（设计 7.2）：AI 原始结果与人工编辑后的候选分开保存；
-- 版本号用于确认时防止后台旧结果覆盖人工修改。

CREATE TABLE parse_drafts (
    id              TEXT NOT NULL PRIMARY KEY,
    capture_id      TEXT NOT NULL REFERENCES captures(id),
    version         INTEGER NOT NULL,
    status          TEXT NOT NULL,
    candidates_json TEXT NOT NULL DEFAULT '[]',
    raw_ai_json     TEXT NULL,
    model           TEXT NULL,
    prompt_version  TEXT NULL,
    schema_version  TEXT NULL,
    attempt_count   INTEGER NOT NULL DEFAULT 0,
    last_error      TEXT NULL,
    created_at_utc  TEXT NOT NULL,
    updated_at_utc  TEXT NOT NULL
);

CREATE INDEX ix_drafts_capture ON parse_drafts (capture_id, version);
CREATE INDEX ix_drafts_status ON parse_drafts (status);
