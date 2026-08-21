#!/bin/bash
# Zero-touch local dev: starts the Postgres dev container, then opens a tmux session with
# Lodge.Server and the Angular dev server each in their own pane — real, separate,
# scrollable logs instead of one interleaved stream. Detach with `Ctrl+B D` to leave both
# running; re-run this script to reattach. `./scripts/run.sh --stop` stops both panes (the
# tmux session); the DB container is untouched either way — `./scripts/dev-db-down.sh`
# tears that down separately. The only file this can create on its own is a root .env,
# copied from .env.example on first run.
#
# Usage: ./scripts/run.sh [--stop]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SESSION="lodge"

if ! command -v tmux >/dev/null 2>&1; then
    echo "tmux is required but not installed (it's a system package, this script won't install it for you)." >&2
    echo "Debian/Ubuntu: sudo apt install tmux   Fedora: sudo dnf install tmux   Arch: sudo pacman -S tmux" >&2
    exit 1
fi

if [ "${1:-}" = "--stop" ]; then
    if tmux has-session -t "$SESSION" 2>/dev/null; then
        tmux kill-session -t "$SESSION"
        echo "==> Stopped tmux session '$SESSION' (server + SPA)."
    else
        echo "==> No '$SESSION' tmux session running."
    fi
    exit 0
fi

# shellcheck source=lib/env.sh
source "$ROOT/scripts/lib/env.sh"
# shellcheck source=lib/dotnet.sh
source "$ROOT/scripts/lib/dotnet.sh"
# shellcheck source=lib/node.sh
source "$ROOT/scripts/lib/node.sh"
# shellcheck source=lib/db.sh
source "$ROOT/scripts/lib/db.sh"

lodge_load_env "$ROOT"

if tmux has-session -t "$SESSION" 2>/dev/null; then
    echo "==> '$SESSION' tmux session already running — attaching (Ctrl+B D to detach)"
    exec tmux attach -t "$SESSION"
fi

echo "== Toolchain =="
lodge_ensure_dotnet "$ROOT"
lodge_ensure_node "$ROOT/ui"

echo "== Database =="
lodge_db_up

echo "==> Starting tmux session '$SESSION': server (left) + SPA (right)"
tmux new-session -d -s "$SESSION" -n dev -c "$ROOT" "./scripts/dev-server.sh"
tmux split-window -h -t "$SESSION:dev" -c "$ROOT" "./scripts/dev-spa.sh"
tmux set-option -t "$SESSION" remain-on-exit on

cat <<EOF

==> Lodge is starting (server migrates itself on first boot — give it a few seconds):
      API+UI  http://localhost:8080
      UI dev  http://localhost:4200  (fast rebuild loop, proxies /api to :8080)
    Ctrl+B D detaches without stopping anything; re-run this script to reattach.
    tmux kill-session -t $SESSION stops both panes. The DB container keeps running
    either way (./scripts/dev-db-down.sh to tear it down).

EOF

exec tmux attach -t "$SESSION"
