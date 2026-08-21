-- Lodge core schema. Collapsed from the prototype's incremental migration history into
-- one clean-slate definition — the effective shape is unchanged, only the history is.

-- A kind onboarded onto Lodge. Multi-kind by design.
CREATE TABLE IF NOT EXISTS kinds (
    code    varchar(64)  PRIMARY KEY,
    name    varchar(256) NOT NULL,
    enabled boolean      NOT NULL DEFAULT false
);

-- A governed instance of a kind. Upserted from inventory YAML every time the
-- reconciliation loop sees the inventory head move.
CREATE TABLE IF NOT EXISTS instances (
    id            uuid         PRIMARY KEY,
    kind_code     varchar(64)  NOT NULL REFERENCES kinds (code) ON DELETE RESTRICT,
    instance_code varchar(128) NOT NULL,
    display_name  varchar(256) NOT NULL DEFAULT '',
    generation    integer      NOT NULL DEFAULT 0,
    region        varchar(64)  NOT NULL DEFAULT '',
    created_at    timestamptz  NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_instances_kind_code_instance_code
    ON instances (kind_code, instance_code);

-- Immutable snapshot of an instance's inventory YAML each time its content changes.
-- Bookkeeping/audit only: action computation reads the latest snapshot, never a diff.
CREATE TABLE IF NOT EXISTS registry_revisions (
    id           uuid         PRIMARY KEY,
    instance_id  uuid         NOT NULL REFERENCES instances (id) ON DELETE CASCADE,
    git_ref      varchar(128) NOT NULL DEFAULT '',
    yaml_content text         NOT NULL DEFAULT '',
    content_hash varchar(128) NOT NULL DEFAULT '',
    created_at   timestamptz  NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_registry_revisions_instance_created
    ON registry_revisions (instance_id, created_at DESC);

-- One runbook invocation. Identity = (instance_id, signal_path, item_key, action_key): the
-- runbook is an execution detail, not part of identity, since the same runbook may back
-- more than one conceptually distinct action. At most one live (QUEUED/RUNNING/FAILED)
-- row exists per identity, and the most recent SUCCEEDED row per identity is the last
-- state confirmed to have happened. policy is a historical record of what the row ran
-- under — the live catalog decides current behavior. desired_value_json is the canonical
-- snapshot the action targets (scalar value for STATE, item body for ADD/MODIFY, 'null'
-- for DELETE).
CREATE TABLE IF NOT EXISTS actions (
    id                   uuid         PRIMARY KEY,
    instance_id          uuid         NOT NULL REFERENCES instances (id) ON DELETE CASCADE,
    capability_code      varchar(128) NOT NULL,
    signal_path          varchar(512) NOT NULL,
    item_key             varchar(512) NULL,
    action_key           varchar(256) NOT NULL,
    runbook_ref          varchar(256) NOT NULL,
    trigger              varchar(16)  NOT NULL, -- STATE | ADD | DELETE | MODIFY
    label                varchar(256) NOT NULL DEFAULT '',
    policy               varchar(32)  NOT NULL, -- AUTO | MANUAL_REQUIRED | OPTIONAL (historical)
    status               varchar(32)  NOT NULL, -- QUEUED | RUNNING | SUCCEEDED | FAILED | SUPERSEDED
    desired_value_json   text         NULL,
    resolved_inputs_json text         NULL,
    pending_prompts_json text         NULL,
    execution_ref        varchar(256) NULL,
    synthetic            boolean      NOT NULL DEFAULT false, -- bootstrap-on-faith adoption
    invalidated_at       timestamptz  NULL,
    invalidated_by       varchar(128) NULL,
    created_at           timestamptz  NOT NULL,
    updated_at           timestamptz  NOT NULL,
    completed_at         timestamptz  NULL
);

-- History projection: latest SUCCEEDED per identity.
CREATE INDEX IF NOT EXISTS ix_actions_history
    ON actions (instance_id, signal_path, action_key, created_at DESC);

-- The reconciler's live-row invariant, enforced by the database.
CREATE UNIQUE INDEX IF NOT EXISTS ux_actions_live
    ON actions (instance_id, signal_path, (COALESCE(item_key, '')), action_key)
    WHERE status IN ('QUEUED', 'RUNNING', 'FAILED');

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

-- The reconciliation loop's head cursor per kind/branch: when the inventory head
-- (branch SHA on GitHub, content hash in local mode) still equals last_seen_sha, the
-- cycle skips re-fetching file contents.
CREATE TABLE IF NOT EXISTS registry_sync_state (
    id            uuid         PRIMARY KEY,
    kind_code     varchar(64)  NOT NULL,
    branch        varchar(128) NOT NULL,
    last_seen_sha varchar(128) NULL,
    updated_at    timestamptz  NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_registry_sync_state_kind_branch
    ON registry_sync_state (kind_code, branch);

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

-- Key/value settings editable from the UI at runtime (reconciliation loop interval,
-- enabled flag). Defaults are applied in code when a key is absent.
CREATE TABLE IF NOT EXISTS settings (
    key        varchar(128) PRIMARY KEY,
    value      text         NOT NULL,
    updated_at timestamptz  NOT NULL
);
