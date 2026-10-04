#!/bin/bash
# Builds the server image locally and runs the full production stack
# (docker-compose.prod.yml: server, Postgres, Keycloak) with .env.prod — the closest local
# approximation to a real deployment. Until the server image is published, this local build
# is the only way to get it: it's tagged as LODGE_SERVER_IMAGE so the compose file picks it
# up. Its state lives in the `lodge-prod` project's named volumes, never in the dev
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
        echo "==> Building $LODGE_SERVER_IMAGE"
        docker build -t "$LODGE_SERVER_IMAGE" -f server/src/Lodge.Server/Dockerfile .
        prod_compose up
        ;;
    down)
        prod_compose down
        ;;
    *)
        echo "usage: $0 [up|down]"
        exit 1
        ;;
esac
