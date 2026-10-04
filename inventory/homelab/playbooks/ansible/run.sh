#!/bin/sh
# Runs one ansible profile (playbooks/<profile>.yml) against one VM — the in-container
# equivalent of the homelab repo's `scripts/ansible.sh <profile> <host>`.
# Inputs: LODGE_PARAM_PROFILE, LODGE_PARAM_HOST (ip or CIDR), and everything in
# LODGE_PARAMS_JSON becomes an extra var (vm_name, vm, vms, ...).
# Secrets: "# pass: VAR" markers in the playbook, resolved through pass-cli (env lookup).
set -eu
. /usr/local/lib/lodge/lodge-lib.sh

profile="${LODGE_PARAM_PROFILE:?missing profile input (the VM has no ansible_profile?)}"
host="$(lodge_host_ip "${LODGE_PARAM_HOST:?missing host input}")"
playbook="/ansible/playbooks/${profile}.yml"
[ -f "$playbook" ] || { echo "unknown profile '$profile' — available: $(ls /ansible/playbooks | sed 's/\.yml$//' | tr '\n' ' ')" >&2; exit 1; }

lodge_ssh_setup

# Every input becomes an extra var — except the Proton Pass token.
params=$(mktemp)
python3 -c 'import json, os, sys
p = json.loads(os.environ.get("LODGE_PARAMS_JSON") or "{}")
p.pop("pass_pat", None)
json.dump(p, sys.stdout)' > "$params"

env_file=$(mktemp)
lodge_pass_env_file "$env_file" $(lodge_pass_markers "$playbook")

echo "== ansible profile '$profile' on $host"
lodge_with_secrets "$env_file" ansible-playbook -i "${host}," -u "$SSH_USER" "$playbook" -e "@${params}" "$@"
