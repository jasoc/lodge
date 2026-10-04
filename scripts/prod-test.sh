#!/bin/bash
# Builds the server image from this checkout and runs the full production stack
# (docker-compose.prod.yml: server, Postgres, Keycloak) with .env.prod — the closest local
# approximation to a real deployment. The build goes through the compose file's `build:`
# (root Dockerfile, repo root as context) and is tagged as LODGE_SERVER_IMAGE, shadowing
# the published image locally until the next pull. Its state lives in the `lodge-prod` project's named volumes, never in the dev
# database (./data/postgres).
#
# Usage: ./scripts/prod-test.sh [up|down]  (default: up)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE="$ROOT/.env.prod"
# shellcheck source=lib/docker.sh
source "$ROOT/scripts/lib/docker.sh"

lodge_ensure_docker
if [ ! -f "$ENV_FILE" ]; then
    echo "error: no .env.prod — copy .env.prod.example and fill in every CHANGEME." >&2
    exit 1
fi

set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a
cd "$ROOT"

prod_compose() {
    docker compose -f docker-compose.prod.yml --env-file "$ENV_FILE" "$@"
}

case "${1:-up}" in
    up)
        prod_compose up --build
        ;;
    down)
        prod_compose down
        ;;
    *)
        echo "usage: $0 [up|down]"
        exit 1
        ;;
esac
