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
