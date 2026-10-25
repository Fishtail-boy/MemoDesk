-- v8：提醒硬化的持久化基础。
-- 1) 历史实例无法确认创建时的计划版本，plan_version 默认 0（未知），绝不回填当前版本；
--    未处理的历史实例一律作废，旧按钮/旧动作会因版本不匹配被拒绝。
-- 2) 交接、租约、恢复次数用于防止重复补发。

ALTER TABLE reminder_occurrences ADD COLUMN handed_off_at_utc TEXT NULL;
ALTER TABLE reminder_occurrences ADD COLUMN channel TEXT NULL;
ALTER TABLE reminder_occurrences ADD COLUMN plan_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE reminder_occurrences ADD COLUMN delivery_attempts INTEGER NOT NULL DEFAULT 0;
ALTER TABLE reminder_occurrences ADD COLUMN recovery_attempts INTEGER NOT NULL DEFAULT 0;
ALTER TABLE reminder_occurrences ADD COLUMN delivery_lease_until_utc TEXT NULL;
ALTER TABLE reminder_occurrences ADD COLUMN delivery_owner TEXT NULL;
ALTER TABLE reminder_occurrences ADD COLUMN cancel_reason TEXT NULL;

UPDATE reminder_occurrences
SET status = 'Cancelled',
    cancel_reason = 'legacy-version-unknown',
    updated_at_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
WHERE status IN ('Pending', 'Requested', 'Uncertain');

-- 工作记录时间线改为从 items 实时派生，不再依赖独立的 WorkOccurred 事件。
DELETE FROM timeline_events WHERE event_type = 'WorkOccurred';

-- 草稿编辑状态（保留用户原始输入）与乐观并发版本。
ALTER TABLE parse_drafts ADD COLUMN edit_state_json TEXT NULL;
ALTER TABLE parse_drafts ADD COLUMN edit_version INTEGER NOT NULL DEFAULT 0;
