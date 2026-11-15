CREATE TABLE digital_assets (
    id TEXT PRIMARY KEY NOT NULL,
    name TEXT NOT NULL CHECK (length(trim(name)) > 0),
    category TEXT NOT NULL CHECK (length(trim(category)) > 0),
    link TEXT,
    description TEXT NOT NULL,
    expiration_date TEXT,
    expiration_time TEXT,
    CHECK (expiration_time IS NULL OR expiration_date IS NOT NULL)
);
