-- 提醒计划与实例（设计 7.4 / 7.5）。
-- 每个事项同一时刻最多一个有效计划（部分唯一索引）。

CREATE TABLE reminders (
    id             TEXT NOT NULL PRIMARY KEY,
    item_id        TEXT NOT NULL REFERENCES items(id),
    reminder_at_utc TEXT NOT NULL,
    enabled        INTEGER NOT NULL,
    plan_version   INTEGER NOT NULL DEFAULT 1,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE UNIQUE INDEX ux_reminders_active ON reminders (item_id) WHERE enabled = 1;
CREATE INDEX ix_reminders_due ON reminders (enabled, reminder_at_utc);

CREATE TABLE reminder_occurrences (
    id                  TEXT NOT NULL PRIMARY KEY,
    reminder_id         TEXT NOT NULL REFERENCES reminders(id),
    item_id             TEXT NOT NULL REFERENCES items(id),
    scheduled_at_utc    TEXT NOT NULL,
    status              TEXT NOT NULL,
    notification_tag    TEXT NOT NULL,
    options_snapshot_json TEXT NULL,
    requested_at_utc    TEXT NULL,
    action              TEXT NULL,
    action_at_utc       TEXT NULL,
    attempt_count       INTEGER NOT NULL DEFAULT 0,
    last_error          TEXT NULL,
    created_at_utc      TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL
);

CREATE INDEX ix_occurrences_due ON reminder_occurrences (status, scheduled_at_utc);
CREATE INDEX ix_occurrences_reminder ON reminder_occurrences (reminder_id, status);
