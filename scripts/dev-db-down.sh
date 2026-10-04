#!/bin/bash
# Stops and removes the dev Postgres container (./data/postgres is kept — pass --wipe to
# also delete it).
# Usage: ./scripts/dev-db-down.sh [--wipe]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/env.sh
source "$ROOT/scripts/lib/env.sh"
# shellcheck source=lib/docker.sh
source "$ROOT/scripts/lib/docker.sh"
# shellcheck source=lib/db.sh
source "$ROOT/scripts/lib/db.sh"

lodge_load_env "$ROOT"
lodge_ensure_docker
if [ "${1:-}" = "--wipe" ]; then
    lodge_db_wipe
else
    lodge_db_down
fi
