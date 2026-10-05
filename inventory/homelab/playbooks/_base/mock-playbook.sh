#!/bin/sh
# The only "implementation" of every homelab playbook in this example inventory: it touches
# nothing, it just says what the real playbook would have done and exits 0. Shared by the
# three playbook images through the `base` additional build context (COPY --from=base).
# Usage (ENTRYPOINT of each image + the capability's `command:`): mock-playbook.sh <tool> [action]
# Input `sleep_seconds` (LODGE_PARAM_SLEEP_SECONDS) makes the run last that long instead of
# the default 2 seconds: the long-running OPTIONAL actions use it (120 = two minutes).
set -eu

tool="${1:-playbook}"
action="${2:-run}"
seconds="${LODGE_PARAM_SLEEP_SECONDS:-2}"

echo "== [mock] ${tool} ${action}: nothing is executed, this is an example inventory"
for name in module unit name vm_name profile host file; do
    value=$(printenv "LODGE_PARAM_$(echo "$name" | tr '[:lower:]' '[:upper:]')" || true)
    [ -z "$value" ] || echo "   ${name}: ${value}"
done

echo "== [mock] working for ${seconds}s"
elapsed=0
while [ "$elapsed" -lt "$seconds" ]; do
    sleep 1
    elapsed=$((elapsed + 1))
    # A heartbeat every 10s so a long run visibly makes progress in the run log.
    if [ $((elapsed % 10)) -eq 0 ]; then
        echo "   ... ${elapsed}/${seconds}s"
    fi
done
echo "== [mock] ${tool} ${action}: done"
