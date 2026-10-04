#!/bin/sh
# Usage (from run.sh, inside the generated root, under `pass-cli run` so the provider
# credentials are already resolved; the state backend's connection string comes from Lodge
# as the tf_pg_conn_str input):
#   tf-run.sh apply|destroy|plan <workspace>
# One unit = one workspace of the Postgres backend, created on first use and deleted on
# destroy.
set -eu

action="$1"
workspace="$2"
common="-input=false -no-color -lock-timeout=5m"
# The pg backend reads its connection string from PG_CONN_STR: it must be exported, and in
# a form Go's lib/pq understands (postgres://user:pass@host:port/db?sslmode=disable).
export PG_CONN_STR="${LODGE_PARAM_TF_PG_CONN_STR:?missing tf_pg_conn_str input (kind.yaml defaults)}"

# The state database is created on first use: connect to the same server's `postgres`
# maintenance database and CREATE DATABASE if it's missing (the user needs CREATEDB). Only
# for the URL form; a concurrent run creating it first is fine.
case "$PG_CONN_STR" in
    postgres://*/*|postgresql://*/*)
        state_db=$(printf '%s' "$PG_CONN_STR" | sed -E 's#^[a-z]+://[^/]*/([^?]*).*#\1#')
        admin_url=$(printf '%s' "$PG_CONN_STR" | sed -E 's#^([a-z]+://[^/]*/)[^?]*#\1postgres#')
        exists() {
            [ "$(printf '%s\n' "SELECT 1 FROM pg_database WHERE datname = :'db';" \
                | psql "$admin_url" -tAX -v db="$state_db" 2>/dev/null)" = 1 ]
        }
        if [ -n "$state_db" ] && ! exists; then
            echo "== creating state database '$state_db'"
            printf '%s\n' "SELECT format('CREATE DATABASE %I', :'db') \\gexec" \
                | psql "$admin_url" -qX -v ON_ERROR_STOP=1 -v db="$state_db" >/dev/null || exists
        fi
        ;;
esac

terraform init -input=false -no-color
terraform workspace select -or-create=true -no-color "$workspace"

case "$action" in
    apply)
        terraform apply -auto-approve $common
        ;;
    destroy)
        terraform destroy -auto-approve $common
        # Nothing left to track: drop the (now empty) workspace.
        terraform workspace select -no-color default
        terraform workspace delete -no-color "$workspace"
        ;;
    plan)
        terraform plan $common
        ;;
    *)
        echo "unknown action '$action' (expected apply, destroy or plan)" >&2
        exit 2
        ;;
esac
