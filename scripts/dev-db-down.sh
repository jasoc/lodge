#!/bin/bash
# Stops and removes the local Postgres dev container (the data volume is kept — pass
# --wipe to also delete it).
# Usage: ./scripts/dev-db-down.sh [--wipe]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/db.sh
source "$ROOT/scripts/lib/db.sh"

lodge_db_down

if [ "${1:-}" = "--wipe" ]; then
    docker volume rm "$LODGE_DB_VOLUME" >/dev/null 2>&1 || true
    echo "==> Data volume wiped"
fi
