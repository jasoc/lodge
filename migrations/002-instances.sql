-- A governed instance of a kind. Upserted from inventory YAML every time the
-- reconciliation loop sees the inventory head move.
CREATE TABLE IF NOT EXISTS instances (
    id            uuid         PRIMARY KEY,
    kind_code     varchar(64)  NOT NULL REFERENCES kinds (code) ON DELETE RESTRICT,
    instance_code varchar(128) NOT NULL,
    display_name  varchar(256) NOT NULL DEFAULT '',
    generation    integer      NOT NULL DEFAULT 0,
    region        varchar(64)  NOT NULL DEFAULT '',
    created_at    timestamptz  NOT NULL,
    -- Instances mirror the inventory like kinds do: one whose folder disappears from
    -- inventory/{kind}/instances/ is disabled (hidden, no longer reconciled), never
    -- deleted, so its action history survives and comes back if the folder does.
    enabled       boolean      NOT NULL DEFAULT true
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_instances_kind_code_instance_code
    ON instances (kind_code, instance_code);
