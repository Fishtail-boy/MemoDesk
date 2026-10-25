-- 正式事项（设计 7.3）：三类业务对象共用主表，便于来源追溯、搜索、提醒与时间线。
-- 业务日期按 YYYY-MM-DD 保存；时刻统一保存 UTC。

CREATE TABLE items (
    id                 TEXT NOT NULL PRIMARY KEY,
    capture_id         TEXT NULL REFERENCES captures(id),
    candidate_source_id TEXT NULL,
    type               TEXT NOT NULL,
    content            TEXT NOT NULL,
    tags_json          TEXT NOT NULL DEFAULT '[]',
    priority           TEXT NULL,
    status             TEXT NOT NULL,
    item_date          TEXT NULL,
    due_at_utc         TEXT NULL,
    memo_date          TEXT NULL,
    work_date          TEXT NULL,
    work_start_time    TEXT NULL,
    work_end_time      TEXT NULL,
    work_end_date      TEXT NULL,
    work_precision     TEXT NULL,
    work_time_text     TEXT NULL,
    created_at_utc     TEXT NOT NULL,
    updated_at_utc     TEXT NOT NULL,
    completed_at_utc   TEXT NULL,
    archived_at_utc    TEXT NULL,
    deleted_at_utc     TEXT NULL,
    row_version        INTEGER NOT NULL DEFAULT 1
);

CREATE UNIQUE INDEX ux_items_source
    ON items (capture_id, candidate_source_id)
    WHERE capture_id IS NOT NULL AND candidate_source_id IS NOT NULL;

CREATE INDEX ix_items_type_status ON items (type, status);
CREATE INDEX ix_items_item_date ON items (item_date);
CREATE INDEX ix_items_memo_date ON items (memo_date);
CREATE INDEX ix_items_work_date ON items (work_date);
