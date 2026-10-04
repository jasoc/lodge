#!/bin/sh
# Usage (Lodge `command:`): verify reachable
#
# reachable: the VM answers on its ssh port. Inputs (LODGE_PARAM_*):
#   host      the VM's address, as in the inventory (`10.0.0.5/24` is fine, the mask is dropped)
#   vm_name   only for the log
#   port      optional, default 22
set -eu

check="${1:?usage: verify reachable}"

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
            exit 1
        fi
        ;;
    *)
        echo "unknown check '$check' (expected reachable)" >&2
        exit 2
        ;;
esac
