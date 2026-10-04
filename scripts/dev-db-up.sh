#!/bin/bash
# Starts (or reuses) the dev Postgres — docker-compose.yml, published on localhost — and waits until it's healthy. ./scripts/run.sh does this for
# you; this is for running the server some other way (IDE, tests). The server migrates
# itself on startup, so there's no separate migration step.
# Usage: ./scripts/dev-db-up.sh
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
lodge_db_up
