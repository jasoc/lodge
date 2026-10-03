#!/bin/bash
# Runs Lodge.Server natively (not in a container) against the dev Postgres (the
# docker-compose.yml `postgres` service — ./scripts/dev-db-up.sh, or ./scripts/run.sh). It migrates itself on startup — nothing else to run first.
# Usage: ./scripts/dev-server.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/env.sh
source "$ROOT/scripts/lib/env.sh"
# shellcheck source=lib/dotnet.sh
source "$ROOT/scripts/lib/dotnet.sh"

lodge_load_env "$ROOT"
lodge_ensure_dotnet "$ROOT"

# The only two things that have to differ from .env's own (docker-compose-shaped)
# defaults: Postgres is reached via its published host port, not the compose network's
# `postgres` hostname, and the listen URL matches what proxy.conf.json expects.
export POSTGRES_HOST=localhost
export ASPNETCORE_URLS="http://localhost:8080"

echo "==> Starting Lodge.Server on $ASPNETCORE_URLS"
cd "$ROOT/server"
exec dotnet run --no-launch-profile --project src/Lodge.Server
