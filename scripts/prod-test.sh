#!/bin/bash
# Builds and runs the full containerized stack (postgres, migrations, server, spa, proxy)
# via docker compose — the closest local approximation to a real deployment. This is the
# only place Docker runs the app itself; local dev (./scripts/run.sh) only containerizes
# the database.
#
# Usage: ./scripts/prod-test.sh [up|down]  (default: up)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/env.sh
source "$ROOT/scripts/lib/env.sh"

lodge_load_env "$ROOT"
cd "$ROOT"

case "${1:-up}" in
    up)
        # Bind-mount dirs created by us, not by the docker daemon as root (see lib/db.sh).
        mkdir -p data/postgres data/runbooks
        docker compose up --build
        ;;
    down)
        docker compose down
        ;;
    *)
        echo "usage: $0 [up|down]"
        exit 1
        ;;
esac
