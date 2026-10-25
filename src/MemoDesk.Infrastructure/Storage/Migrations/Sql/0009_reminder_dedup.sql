-- v9：提醒去重、人工重发、计划通知持久化与撤销任务。

-- 1) 人工重发计数
ALTER TABLE reminder_occurrences ADD COLUMN manual_attempts INTEGER NOT NULL DEFAULT 0;

-- 2) 同一计划版本只允许一个正版本实例。
-- 历史重复按“交接证据优先”保留：已交接 > Completed/Snoozed > Requested > Failed > Uncertain > Pending > Cancelled；
-- 同优先级取创建时间最早、rowid 最小。其余作废并移为负版本，保留历史但不参与唯一匹配。
WITH ranked AS (
    SELECT rowid AS rid,
           ROW_NUMBER() OVER (
               PARTITION BY reminder_id, plan_version
               ORDER BY
                   CASE
                       WHEN handed_off_at_utc IS NOT NULL THEN 0
                       WHEN status IN ('Completed', 'Snoozed') THEN 1
                       WHEN status = 'Requested' THEN 2
                       WHEN status = 'Failed' THEN 3
                       WHEN status = 'Uncertain' THEN 4
                       WHEN status = 'Pending' THEN 5
                       ELSE 6
                   END,
                   created_at_utc ASC,
                   rowid ASC
           ) AS rn
    FROM reminder_occurrences
    WHERE plan_version > 0
)
UPDATE reminder_occurrences
SET status = 'Cancelled',
    cancel_reason = 'duplicate-plan-version',
    plan_version = -rowid,
    updated_at_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
WHERE rowid IN (SELECT rid FROM ranked WHERE rn > 1);

CREATE UNIQUE INDEX ux_occurrences_plan_version
    ON reminder_occurrences (reminder_id, plan_version)
    WHERE plan_version > 0;

-- 3) 计划通知登记状态
ALTER TABLE reminders ADD COLUMN delivery_owner TEXT NULL;
ALTER TABLE reminders ADD COLUMN scheduled_tag TEXT NULL;
ALTER TABLE reminders ADD COLUMN scheduled_at_utc TEXT NULL;
ALTER TABLE reminders ADD COLUMN schedule_state TEXT NULL;
ALTER TABLE reminders ADD COLUMN schedule_plan_version INTEGER NULL;
ALTER TABLE reminders ADD COLUMN schedule_error TEXT NULL;
ALTER TABLE reminders ADD COLUMN schedule_updated_at TEXT NULL;

-- 4) 旧通知撤销任务：独立于计划当前版本，直到撤销成功。
CREATE TABLE notification_cancellations (
    id               TEXT NOT NULL PRIMARY KEY,
    reminder_id      TEXT NOT NULL REFERENCES reminders(id),
    plan_version     INTEGER NOT NULL,
    notification_tag TEXT NOT NULL,
    reason           TEXT NOT NULL,
    state            TEXT NOT NULL,
    attempts         INTEGER NOT NULL DEFAULT 0,
    last_error       TEXT NULL,
    created_at_utc   TEXT NOT NULL,
    updated_at_utc   TEXT NOT NULL
);

CREATE INDEX ix_notification_cancellations_state
    ON notification_cancellations (state, created_at_utc);
CREATE UNIQUE INDEX ux_notification_cancellations_tag
    ON notification_cancellations (notification_tag)
    WHERE state <> 'cancelled';
