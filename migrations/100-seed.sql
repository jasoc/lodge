-- Baseline data: the enabled kinds. Capability catalogs and runbook permissions are
-- read from inventory/ at runtime (Git stays the single source of truth), never seeded
-- here. `acme` is the invented example kind shipped with Lodge for the demo/vertical
-- slice — replace it with your own kind(s).
INSERT INTO kinds (code, name, enabled)
VALUES ('acme', 'ACME Platform', true)
ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name, enabled = EXCLUDED.enabled;
