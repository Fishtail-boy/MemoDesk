-- 时间线事件（设计 7.6）：工作记录以自身发生时间参与；完成事件保留内容快照。

CREATE TABLE timeline_events (
    id               TEXT NOT NULL PRIMARY KEY,
    item_id          TEXT NULL REFERENCES items(id),
    event_type       TEXT NOT NULL,
    occurred_at_utc  TEXT NOT NULL,
    recorded_at_utc  TEXT NOT NULL,
    content_snapshot TEXT NOT NULL
);

CREATE INDEX ix_timeline_occurred ON timeline_events (occurred_at_utc);
CREATE INDEX ix_timeline_item ON timeline_events (item_id);
