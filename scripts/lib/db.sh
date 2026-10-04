#!/bin/bash
# Manages the dev Postgres: the only service of the root docker-compose.yml, published on
# localhost. Docker is used here only for the database — the server runs natively (see
# dev-server.sh) and migrates itself on startup. Data lives in ./data/postgres (the compose
# bind mount). Expects lodge_load_env to have run (compose reads .env itself, but POSTGRES_*
# must be exported for the readiness check).

# Runs `docker compose` against the dev compose file from the repo root.
lodge_db_compose() {
    local root
    root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
    (cd "$root" && docker compose -f docker-compose.yml "$@")
}

lodge_db_up() {
    local root
    root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
    # Pre-create the bind-mount dir as us: a ./data docker creates itself is owned by root,
    # and the natively running server then can't write its run logs under it.
    mkdir -p "$root/data/postgres"
    echo "==> Starting Postgres (docker compose service 'postgres')"
    lodge_db_compose up -d --wait postgres
}

lodge_db_down() {
    lodge_db_compose rm --stop --force postgres >/dev/null
    echo "==> Postgres container removed (./data/postgres kept)"
}

lodge_db_wipe() {
    local root
    root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
    lodge_db_compose rm --stop --force postgres >/dev/null
    # The files belong to the container's postgres uid, not to us — delete them from a
    # throwaway container instead of asking for sudo.
    docker run --rm -v "$root/data:/data" postgres:16-alpine rm -rf /data/postgres
    echo "==> Postgres container and ./data/postgres removed"
}
