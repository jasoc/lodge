#!/bin/bash
# Runs Lodge.Server natively (not in a container), with hot reload, against the dev Postgres (the
# docker-compose.yml — ./scripts/dev-db-up.sh, or ./scripts/run.sh). It migrates itself on
# startup — nothing else to run first. Requires the .NET SDK pinned in global.json.
# Usage: ./scripts/dev-server.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/env.sh
source "$ROOT/scripts/lib/env.sh"
# shellcheck source=lib/dotnet.sh
source "$ROOT/scripts/lib/dotnet.sh"

lodge_load_env "$ROOT"
lodge_ensure_dotnet "$ROOT"

# Postgres is reached via its published host port, and the listen URL matches what
# proxy.conf.json expects.
export POSTGRES_HOST=localhost
export ASPNETCORE_URLS="http://localhost:8080"

echo "==> Starting Lodge.Server on $ASPNETCORE_URLS (dotnet watch: edits to server code apply live)"
cd "$ROOT/server"
# `dotnet watch` rebuilds and hot-reloads on every edit under server/ (the Lodge.Core and
# Lodge.Infrastructure projects included); an edit hot reload can't apply, a "rude edit"
# such as a changed signature, restarts the server by itself. --non-interactive: no prompt to
# answer, this runs under process-compose.
export DOTNET_WATCH_SUPPRESS_EMOJIS=1
exec dotnet watch run --non-interactive --no-launch-profile --project src/Lodge.Server
