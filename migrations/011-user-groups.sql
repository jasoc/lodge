-- A user's groups (see users), replaced from the token's groups claim at each login.
CREATE TABLE IF NOT EXISTS user_groups (
    user_id     varchar(128) NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    group_name  varchar(256) NOT NULL,
    PRIMARY KEY (user_id, group_name)
);

CREATE INDEX IF NOT EXISTS ix_user_groups_group_name ON user_groups (group_name);
