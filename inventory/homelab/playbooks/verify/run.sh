#!/bin/sh
# Usage (Lodge `command:`): verify reachable
#
# Lodge compares the inventory with the history of what it ran, not with reality. This is the
# pattern for closing that gap, kept out of the engine: an OPTIONAL action looks at reality and,
# when it disagrees with what Lodge believes was done, *invalidates* the matching SUCCEEDED
# action through Lodge's own API. The next reconciliation cycle then sees that action as
# never done and offers it again, exactly as if the inventory had asked for it.
#
#   reachable   the VM answers on its ssh port. If not, and the invalidate_* inputs are given,
#               the VM's SUCCEEDED `invalidate_action` is invalidated; either way the run
#               fails, so the red run on the card says why.
#
# Inputs (LODGE_PARAM_*):
#   host                the VM's address as in the inventory (`10.0.0.5/24` is fine)
#   vm_name             only for the log
#   port                optional, default 22
#   lodge_url           where to reach Lodge from this container; with lodge_token and the
#   lodge_token         three invalidate_* below, enables the invalidation (a service token
#   invalidate_signal   with the `actions` scope; the token is masked in the run log)
#   invalidate_key      the identity of the action to invalidate:
#   invalidate_action   signal path, item key, action key
set -eu

check="${1:?usage: verify reachable}"

# Invalidates the newest valid SUCCEEDED action with the given identity.
invalidate() {
    base="${LODGE_PARAM_LODGE_URL%/}/api/v1/kinds/$LODGE_KIND_CODE/instances/$LODGE_INSTANCE_CODE/actions"
    actions=$(curl -fsS -H "Authorization: Bearer $LODGE_PARAM_LODGE_TOKEN" "$base")
    id=$(printf '%s' "$actions" | jq -r \
        --arg signal "$LODGE_PARAM_INVALIDATE_SIGNAL" --arg key "$LODGE_PARAM_INVALIDATE_KEY" --arg action "$LODGE_PARAM_INVALIDATE_ACTION" '
        [.[] | select(.signal_path == $signal and .item_key == $key and .action_key == $action
                      and .status == "SUCCEEDED" and .invalidated_at == null)]
        | sort_by(.completed_at) | last | .id // empty')
    if [ -z "$id" ]; then
        echo "nothing to invalidate: no valid SUCCEEDED $LODGE_PARAM_INVALIDATE_ACTION for $LODGE_PARAM_INVALIDATE_KEY" >&2
        return 0
    fi
    curl -fsS -X POST -H "Authorization: Bearer $LODGE_PARAM_LODGE_TOKEN" -H 'Content-Type: application/json' \
        -d '{"actor":"verify"}' "$base/$id/invalidate" >/dev/null
    echo "invalidated $LODGE_PARAM_INVALIDATE_ACTION ($id): Lodge will offer it again on its next cycle" >&2
}

case "$check" in
    reachable)
        host="${LODGE_PARAM_HOST:?missing host input}"
        host="${host%%/*}"
        port="${LODGE_PARAM_PORT:-22}"
        echo "== ${LODGE_PARAM_VM_NAME:-$host}: ssh reachable on $host:$port?"
        if nc -z -w 5 "$host" "$port"; then
            echo "yes"
        else
            echo "NO: nothing answers on $host:$port" >&2
            if [ -n "${LODGE_PARAM_LODGE_URL:-}" ] && [ -n "${LODGE_PARAM_LODGE_TOKEN:-}" ] && [ -n "${LODGE_PARAM_INVALIDATE_ACTION:-}" ]; then
                invalidate
            fi
            exit 1
        fi
        ;;
    *)
        echo "unknown check '$check' (expected reachable)" >&2
        exit 2
        ;;
esac
