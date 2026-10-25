-- M0 初始结构：仅建立应用元信息表，验证迁移通道。
-- 后续里程碑按需追加 NNNN_*.sql，不得修改已发布的迁移脚本。

CREATE TABLE app_meta (
    key   TEXT NOT NULL PRIMARY KEY,
    value TEXT NOT NULL
);

INSERT INTO app_meta (key, value)
VALUES ('schema_created_at', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
