#!/bin/bash
# Loads (creating from .env.example on first run) the repo-root .env file and exports
# every variable in it, so all scripts see the same configuration.

lodge_load_env() {
    local root="$1"
    local env_file="$root/.env"
    local example_file="$root/.env.example"

    if [ ! -f "$env_file" ]; then
        echo "==> No .env found, creating one from .env.example with defaults"
        cp "$example_file" "$env_file"
    fi

    set -a
    # shellcheck disable=SC1090
    source "$env_file"
    set +a
}
