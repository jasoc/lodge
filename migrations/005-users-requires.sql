-- Who may run what is now decided per action by its catalog `requires: <group>` (absent or
-- `nobody` = anyone), checked against the caller's groups. The runbook reference and the
-- per-kind runbook-permissions.yaml it keyed are retired; what runs is the action's
-- executor block alone (docker | http).
ALTER TABLE actions
    DROP COLUMN IF EXISTS runbook_ref,
    ADD COLUMN IF NOT EXISTS requires varchar(256) NULL;

-- Shell/Webhook executors are gone; every existing row ran under Docker.
ALTER TABLE actions ALTER COLUMN executor_kind SET DEFAULT 'Docker';

-- People and their groups. With OIDC both are mirrored from the identity provider at each
-- login (group names 1:1 with the token's groups claim); locally they're edited in Lodge.
-- A personal token's groups are read from here on every request, so a membership change
-- applies at once instead of at the next login.
CREATE TABLE IF NOT EXISTS users (
    id             varchar(128) PRIMARY KEY, -- token subject id
    display_name   varchar(256) NOT NULL DEFAULT '',
    source         varchar(16)  NOT NULL DEFAULT 'local', -- local | oidc
    created_at     timestamptz  NOT NULL,
    last_login_at  timestamptz  NULL
);

CREATE TABLE IF NOT EXISTS user_groups (
    user_id     varchar(128) NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    group_name  varchar(256) NOT NULL,
    PRIMARY KEY (user_id, group_name)
);

CREATE INDEX IF NOT EXISTS ix_user_groups_group_name ON user_groups (group_name);
