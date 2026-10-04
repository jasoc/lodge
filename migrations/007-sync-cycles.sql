-- One row per reconciliation cycle (timer tick, manual, or API-triggered run).
CREATE TABLE IF NOT EXISTS sync_cycles (
    id                     uuid        PRIMARY KEY,
    started_at             timestamptz NOT NULL,
    completed_at           timestamptz NULL,
    triggered_by           varchar(32) NOT NULL, -- Timer | Manual | Api
    success                boolean     NOT NULL DEFAULT false,
    kinds_checked          integer     NOT NULL DEFAULT 0,
    instances_reconciled   integer     NOT NULL DEFAULT 0,
    drift_count            integer     NOT NULL DEFAULT 0,
    error                  text        NULL,
    messages_json          text        NOT NULL DEFAULT '[]',
    validation_errors_json text        NOT NULL DEFAULT '[]'
);

CREATE INDEX IF NOT EXISTS ix_sync_cycles_started_at
    ON sync_cycles (started_at);
