-- 原始输入表（设计 7.1）：原文按用户输入原样保存，捕获时刻以 UTC 存储，
-- 同时保存捕获时区；解析状态与最近错误供 M2 的解析链路使用。

CREATE TABLE captures (
    id                    TEXT NOT NULL PRIMARY KEY,
    raw_text              TEXT NOT NULL,
    captured_at_utc       TEXT NOT NULL,
    captured_time_zone_id TEXT NOT NULL,
    parse_status          TEXT NOT NULL,
    last_error            TEXT NULL,
    recorded_at_utc       TEXT NOT NULL
);

CREATE INDEX ix_captures_parse_status ON captures (parse_status);
