#!/bin/bash
# Runs the Angular dev server (fast rebuild/HMR loop). Its proxy.conf.json forwards /api
# to Lodge.Server on :8080 — start ./scripts/dev-server.sh first (or use ./scripts/run.sh
# to get the whole stack under process-compose).
# Usage: ./scripts/dev-spa.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UI_DIR="$ROOT/ui"
# shellcheck source=lib/node.sh
source "$ROOT/scripts/lib/node.sh"

lodge_ensure_node "$UI_DIR"

cd "$UI_DIR"
if [ ! -d node_modules ]; then
    echo "==> Installing SPA dependencies (npm ci)"
    npm ci
fi

echo "==> Starting the Angular dev server on http://localhost:4200"
exec npx ng serve
