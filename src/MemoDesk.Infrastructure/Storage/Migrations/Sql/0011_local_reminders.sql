-- 新录入的明确相对提醒与备忘原子保存；仅新增元数据，不重跑历史解析。
ALTER TABLE reminders ADD COLUMN source TEXT NOT NULL DEFAULT 'Legacy';
ALTER TABLE reminders ADD COLUMN relative_seconds INTEGER NULL;
ALTER TABLE reminders ADD COLUMN captured_at_utc TEXT NULL;
ALTER TABLE reminders ADD COLUMN source_time_text TEXT NULL;
