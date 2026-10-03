-- Instances mirror the inventory like kinds do: an instance whose folder disappears from
-- inventory/{kind}/instances/ is disabled (hidden, no longer reconciled), never deleted, so
-- its action history survives and comes back if the folder does.
ALTER TABLE instances
    ADD COLUMN IF NOT EXISTS enabled boolean NOT NULL DEFAULT true;

-- Instance enablement is only re-evaluated when a kind's inventory head moves; forget the
-- last seen heads once so the first cycle after this migration evaluates every instance.
UPDATE registry_sync_state SET last_seen_sha = NULL;
