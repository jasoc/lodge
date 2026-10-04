-- Users now come only from the identity provider (OIDC); the no-auth profile has a single
-- implicit admin and no user records. Drop any local users created in between.
DELETE FROM users WHERE source = 'local';
