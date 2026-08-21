#!/bin/bash
# Manages the local Postgres dev container. Docker is used here only for the database —
# the server runs natively (see dev-server.sh) and migrates itself on startup, so there's
# no separate migration step here. Idempotent: safe to call lodge_db_up repeatedly.

LODGE_DB_CONTAINER="lodge-postgres-dev"
LODGE_DB_VOLUME="lodge-postgres-dev-data"

lodge_db_up() {
    if docker inspect "$LODGE_DB_CONTAINER" >/dev/null 2>&1; then
        if [ "$(docker inspect -f '{{.State.Running}}' "$LODGE_DB_CONTAINER")" = "true" ]; then
            echo "==> Postgres dev container already running"
        else
            echo "==> Starting existing Postgres dev container"
            docker start "$LODGE_DB_CONTAINER" >/dev/null
        fi
    else
        echo "==> Creating Postgres dev container ($LODGE_DB_CONTAINER)"
        docker run -d --name "$LODGE_DB_CONTAINER" \
            -e POSTGRES_DB="$POSTGRES_DB" \
            -e POSTGRES_USER="$POSTGRES_USER" \
            -e POSTGRES_PASSWORD="$POSTGRES_PASSWORD" \
            -p "${POSTGRES_PORT}:5432" \
            -v "${LODGE_DB_VOLUME}:/var/lib/postgresql/data" \
            postgres:16-alpine >/dev/null
    fi

    echo -n "==> Waiting for Postgres to be ready"
    for _ in $(seq 1 30); do
        if docker exec "$LODGE_DB_CONTAINER" pg_isready -U "$POSTGRES_USER" >/dev/null 2>&1; then
            echo " ready"
            return 0
        fi
        echo -n "."
        sleep 1
    done
    echo " timed out waiting for Postgres"
    return 1
}

lodge_db_down() {
    docker rm -f "$LODGE_DB_CONTAINER" >/dev/null 2>&1 || true
    echo "==> Postgres dev container removed (data volume kept: $LODGE_DB_VOLUME)"
}
