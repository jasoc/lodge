-- People and their groups, mirrored from the identity provider (OIDC) at each login, group
-- names 1:1 with the token's groups claim; the no-auth profile has a single implicit admin
-- and no user records. A personal token's groups are read from here on every request, so
-- a membership change applies at once instead of at the next login.
CREATE TABLE IF NOT EXISTS users (
    id             varchar(128) PRIMARY KEY, -- token subject id
    display_name   varchar(256) NOT NULL DEFAULT '',
    source         varchar(16)  NOT NULL DEFAULT 'oidc',
    created_at     timestamptz  NOT NULL,
    last_login_at  timestamptz  NULL
);
