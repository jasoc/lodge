#!/bin/bash
# Local dev: runs Postgres (docker-compose.yml), Lodge.Server
# and the Angular dev server under process-compose (process-compose.yaml at the repo root),
# with a TUI showing each one's status and its own scrollable log.
#
# The stack runs detached from the TUI: quitting the TUI leaves everything running, and
# re-running this script reattaches. `./scripts/run.sh --stop` stops the whole stack,
# Postgres included (its data stays in ./data/postgres — `./scripts/dev-db-down.sh --wipe`
# deletes it). The only file this can create on its own is a root .env, copied from
# .env.example on first run.
#
# It never installs tools: it checks that docker, process-compose, the .NET SDK pinned in
# global.json and the Node pinned in ui/.nvmrc are available, and stops with a pointer to
# the README's Prerequisites if one isn't.
#
# Usage: ./scripts/run.sh [--stop]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOCKET="$ROOT/.run/process-compose.sock"

# shellcheck source=lib/env.sh
source "$ROOT/scripts/lib/env.sh"
# shellcheck source=lib/docker.sh
source "$ROOT/scripts/lib/docker.sh"
# shellcheck source=lib/dotnet.sh
source "$ROOT/scripts/lib/dotnet.sh"
# shellcheck source=lib/node.sh
source "$ROOT/scripts/lib/node.sh"
# shellcheck source=lib/process-compose.sh
source "$ROOT/scripts/lib/process-compose.sh"

lodge_stack_running() {
    [ -S "$SOCKET" ] && process-compose list -U -u "$SOCKET" >/dev/null 2>&1
}

lodge_ensure_process_compose
mkdir -p "$ROOT/.run"

if [ "${1:-}" = "--stop" ]; then
    if lodge_stack_running; then
        process-compose down -U -u "$SOCKET"
        echo "==> Stopped the dev stack (postgres, server, SPA)."
    else
        echo "==> No dev stack running."
    fi
    exit 0
fi

if lodge_stack_running; then
    echo "==> Dev stack already running — attaching"
    exec process-compose attach -U -u "$SOCKET"
fi
# A socket left behind by a crashed run would make the new server fail to bind.
rm -f "$SOCKET"

lodge_load_env "$ROOT"

echo "== Toolchain =="
lodge_ensure_docker
lodge_ensure_dotnet "$ROOT"
lodge_ensure_node "$ROOT/ui"

cat <<EOF2

==> Starting the dev stack (server migrates itself on first boot):
      API+UI  http://localhost:8080
      UI dev  http://localhost:4200  (fast rebuild loop, proxies /api to :8080)
    Quitting the TUI leaves the stack running; re-run this script to reattach,
    ./scripts/run.sh --stop to stop everything.

EOF2

cd "$ROOT"
exec process-compose up -f process-compose.yaml -U -u "$SOCKET" --detached-with-tui
