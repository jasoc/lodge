-- One action run. Identity = (instance_id, signal_path, item_key, action_key): what runs
-- (the executor block) is an execution detail, not part of identity, since the same
-- container may back more than one conceptually distinct action. At most one live
-- (BLOCKED/QUEUED/RUNNING/FAILED) row exists per identity, and the most recent SUCCEEDED row per identity is the last
-- state confirmed to have happened. policy is a historical record of what the row ran
-- under — the live catalog decides current behavior. desired_value_json is the canonical
-- snapshot the action targets (scalar value for STATE, item body for ADD/MODIFY, 'null'
-- for DELETE). executor_kind/executor_config_json are what runs (container | http, and
-- its config JSON — part of the snapshot an approval is pinned to); secret_inputs_json the
-- unresolved secret references, resolved to plaintext only by ActionExecutionService;
-- requires the group whose members alone may confirm/retry/revoke it (null = anyone);
-- depends_on_json the identities of the actions it waits for (the action graph's edges).
CREATE TABLE IF NOT EXISTS actions (
    id                   uuid         PRIMARY KEY,
    instance_id          uuid         NOT NULL REFERENCES instances (id) ON DELETE CASCADE,
    capability_code      varchar(128) NOT NULL,
    signal_path          varchar(512) NOT NULL,
    item_key             varchar(512) NULL,
    action_key           varchar(256) NOT NULL,
    trigger              varchar(16)  NOT NULL, -- STATE | ADD | DELETE | MODIFY
    label                varchar(256) NOT NULL DEFAULT '',
    policy               varchar(32)  NOT NULL, -- AUTO | MANUAL_REQUIRED | OPTIONAL (historical)
    status               varchar(32)  NOT NULL, -- BLOCKED | QUEUED | RUNNING | SUCCEEDED | FAILED | SUPERSEDED
    desired_value_json   text         NULL,
    resolved_inputs_json text         NULL,
    pending_prompts_json text         NULL,
    execution_ref        varchar(256) NULL,
    synthetic            boolean      NOT NULL DEFAULT false, -- bootstrap-on-faith adoption
    executor_kind        varchar(32)  NOT NULL DEFAULT 'Container', -- Container | Http
    executor_config_json text         NULL,
    secret_inputs_json   text         NULL, -- name -> secret ref map, unresolved
    requires             varchar(256) NULL,
    depends_on_json      text         NULL, -- [{SignalPath, ItemKey, ActionKey}]
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
    WHERE status IN ('BLOCKED', 'QUEUED', 'RUNNING', 'FAILED');
