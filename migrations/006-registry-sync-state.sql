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
