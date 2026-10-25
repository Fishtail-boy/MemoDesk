-- 全局设置（设计 7.7）：键值存储；API Key 只保存经 DPAPI 保护后的值。

CREATE TABLE app_settings (
    key   TEXT NOT NULL PRIMARY KEY,
    value TEXT NOT NULL
);
