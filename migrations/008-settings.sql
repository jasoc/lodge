-- Key/value settings editable from the UI at runtime (reconciliation loop interval,
-- enabled flag). Defaults are applied in code when a key is absent.
CREATE TABLE IF NOT EXISTS settings (
    key        varchar(128) PRIMARY KEY,
    value      text         NOT NULL,
    updated_at timestamptz  NOT NULL
);
