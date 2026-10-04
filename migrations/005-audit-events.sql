-- Immutable audit log. Deliberately no FK to instances: audit history outlives anything.
CREATE TABLE IF NOT EXISTS audit_events (
    id           uuid         PRIMARY KEY,
    instance_id  uuid         NULL,
    kind_code    varchar(64)  NOT NULL DEFAULT '',
    event_type   varchar(128) NOT NULL,
    actor        varchar(128) NOT NULL DEFAULT '',
    payload_json text         NULL,
    created_at   timestamptz  NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_audit_events_instance_id
    ON audit_events (instance_id);
