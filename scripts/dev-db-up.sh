#!/bin/bash
# Starts (or reuses) the local Postgres dev container. The server migrates itself on
# startup, so there's no separate migration step to run here.
# Usage: ./scripts/dev-db-up.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/env.sh
source "$ROOT/scripts/lib/env.sh"
# shellcheck source=lib/db.sh
source "$ROOT/scripts/lib/db.sh"

lodge_load_env "$ROOT"
lodge_db_up
