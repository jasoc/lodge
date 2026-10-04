-- Bearer tokens minted by Lodge itself, for both auth planes: personal tokens (a human,
-- via `lodge login` — auto-approved in the no-auth profile, OIDC-derived otherwise) and
-- service tokens (an automation actor, e.g. a CI job calling `lodge reconcile`). The
-- authentication *mechanism* is identical for both once a token exists: a bearer token
-- validated against this table. Only how a token gets issued differs. We only ever store
-- a hash of the token, never the raw value.
CREATE TABLE IF NOT EXISTS api_tokens (
    id            uuid         PRIMARY KEY,
    token_hash    varchar(128) NOT NULL,
    kind          varchar(32)  NOT NULL, -- personal | service
    display_name  varchar(256) NOT NULL DEFAULT '',
    subject_id    varchar(128) NOT NULL, -- identity this token authenticates as
    groups_json   text         NOT NULL DEFAULT '[]',
    is_admin      boolean      NOT NULL DEFAULT false,
    scopes_json   text         NOT NULL DEFAULT '[]', -- service tokens only; ignored for personal
    created_at    timestamptz  NOT NULL,
    expires_at    timestamptz  NULL,
    revoked_at    timestamptz  NULL,
    last_used_at  timestamptz  NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_api_tokens_token_hash
    ON api_tokens (token_hash);

CREATE INDEX IF NOT EXISTS ix_api_tokens_subject_id
    ON api_tokens (subject_id);
