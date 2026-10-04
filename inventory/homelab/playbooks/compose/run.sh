#!/bin/sh
# Usage (Lodge `command:`): run.sh deploy|remove
# One compose file of inventory/homelab/stacks/ on one VM's docker daemon, over ssh —
# the in-container equivalent of the homelab repo's `compose.sh --proton <host> <file> ...`.
#   deploy — `compose up -d --remove-orphans`
#   remove — `compose down --remove-orphans`
# Inputs: LODGE_PARAM_FILE (path under stacks/), LODGE_PARAM_CONTENT (the file itself —
# for remove, the last deployed version, even if the file is gone from the inventory),
# LODGE_PARAM_HOST (the VM's ip or CIDR).
# The project name is the file's top-level `name:` — the same one the homelab repo's
# compose.sh gives it, so running stacks are updated in place, never duplicated. A file
# without one falls back to its own name (pihole.yml -> "pihole"), never the folder's.
# Secrets: every ${VAR} in the file resolves from pass://$PASS_PREFIX/VAR through pass-cli.
set -eu
. /usr/local/lib/lodge/lodge-lib.sh

action="${1:?usage: run.sh deploy|remove}"
file="${LODGE_PARAM_FILE:?missing file input}"
host="$(lodge_host_ip "${LODGE_PARAM_HOST:?missing host input}")"
case "$file" in /*|*..*) echo "file '$file' must be a relative path inside stacks/" >&2; exit 1 ;; esac

compose_file="/work/stacks/${file}"
mkdir -p "$(dirname "$compose_file")"
printf '%s\n' "${LODGE_PARAM_CONTENT:?missing content input}" > "$compose_file"
project_args=""
if ! grep -qE '^name:[[:space:]]*[^[:space:]]' "$compose_file"; then
    stem=$(basename "$compose_file"); stem=${stem%.*}
    project_args="-p $stem"
fi

lodge_ssh_setup
export DOCKER_HOST="ssh://${SSH_USER}@${host}"
export COMPOSE_BAKE=true

# ${VAR} / ${VAR:-default} references -> pass:// lookups ($$ is compose's escape).
vars=$(sed 's/\$\$//g' "$compose_file" | grep -oE '\$\{[A-Za-z_][A-Za-z0-9_]*' | sed 's/^\${//' | sort -u || true)
env_file=$(mktemp)
lodge_pass_env_file "$env_file" $vars

case "$action" in
    deploy)
        echo "== deploying stacks/$file on $host"
        lodge_with_secrets "$env_file" docker compose $project_args -f "$compose_file" up -d --remove-orphans
        ;;
    remove)
        echo "== removing stacks/$file from $host"
        lodge_with_secrets "$env_file" docker compose $project_args -f "$compose_file" down --remove-orphans
        ;;
    *)
        echo "unknown action '$action' (expected deploy or remove)" >&2
        exit 2
        ;;
esac
