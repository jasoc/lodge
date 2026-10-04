#!/bin/sh
# Usage (Lodge `command:`): run.sh apply|destroy|plan|validate
# (validate: generate the root and `terraform validate` it — no secrets, no ssh, no state.)
#
# Applies ONE module of /tf/modules as its own root module, with its own state — one Lodge
# unit (one inventory item) per state, so a whole-state apply/destroy is always exactly
# that unit, never anything else. States live in Postgres (root.tf: backend "pg", one
# workspace per unit, named after it); tf-run.sh does the terraform part. The root is
# generated per run:
#
#   root.tf + versions.tf         backend + every pinned provider
#   providers/<p>.tf              the provider blocks whose source the module uses
#   unit.tf.json                  module "unit" { source = "./modules/<module>", ...args }
#   import.tf                     only with an import_id: adopts an existing resource
#
# Inputs (LODGE_PARAM_* / LODGE_PARAMS_JSON):
#   module     which module                         unit       state name (an inventory path)
#   inputs     an object of module arguments        <name>     any other input = an argument
#   import_id  optional, see the module's "lodge-import" comment
# Arguments the module doesn't declare are dropped (e.g. a VM's ansible_profile), so a
# capability can hand over a whole inventory item. A module variable with a "# pass: VAR"
# marker is fed from Proton Pass (TF_VAR_<var>), like provider credentials are.
set -eu
. /usr/local/lib/lodge/lodge-lib.sh

action="${1:?usage: run.sh apply|destroy|plan|validate}"
module="${LODGE_PARAM_MODULE:?missing module input}"
unit="${LODGE_PARAM_UNIT:?missing unit input}"
params="${LODGE_PARAMS_JSON:-}"
[ -n "$params" ] || params='{}'

module_dir="/tf/modules/${module}"
[ -d "$module_dir" ] || { echo "unknown module '$module' — available: $(ls /tf/modules | tr '\n' ' ')" >&2; exit 1; }
workspace=$(printf '%s' "$unit" | tr -c 'A-Za-z0-9._-' '_')

# --- generate the root -----------------------------------------------------------------
root=/work/root
rm -rf "$root"; mkdir -p "$root/modules"
cp /tf/root.tf /tf/versions.tf /tf/.terraform.lock.hcl "$root/"
cp -r "$module_dir" "$root/modules/$module"

uses_proxmox=0
for provider_file in /tf/providers/*.tf; do
    source=$(grep -hoE '^# lodge-provider-source: [^ ]+' "$provider_file" | sed 's/^# lodge-provider-source: //')
    if grep -qF "\"$source\"" "$module_dir"/*.tf; then
        cp "$provider_file" "$root/"
        [ "$source" = "bpg/proxmox" ] && uses_proxmox=1
    fi
done

variables=$(grep -hoE '^variable "[A-Za-z0-9_]+"' "$module_dir"/*.tf | sed -E 's/variable "(.*)"/\1/' | jq -R . | jq -sc .)
pass_vars=$(lodge_pass_markers "$module_dir"/*.tf | tr '[:upper:]' '[:lower:]' | jq -R . | jq -sc 'map(select(length > 0))')

jq -n \
    --arg source "./modules/${module}" \
    --argjson params "$params" \
    --argjson variables "$variables" \
    --argjson pass_vars "$pass_vars" '
    # Literal "${" / "%{" in values must not become Terraform template syntax.
    def escape: walk(if type == "string" then gsub("\\$\\{"; "$${") | gsub("%\\{"; "%%{") else . end);
    (($params.inputs // {}) + ($params | del(.inputs, .module, .unit, .import_id, .pass_pat))
        | with_entries(select(.key as $k | ($variables | index($k)) and ($pass_vars | index($k) | not)))
        | escape) as $args
    | {module: {unit: ({source: $source} + $args + ($pass_vars | map({(.): ("${var." + . + "}")}) | add // {}))}}
    # An empty "variable" object is not valid Terraform JSON: only add it when needed.
    + (if ($pass_vars | length) > 0
       then {variable: ($pass_vars | map({(.): {type: "string", sensitive: true}}) | add)}
       else {} end)' > "$root/unit.tf.json"

import_id="${LODGE_PARAM_IMPORT_ID:-}"
if [ -n "$import_id" ] && [ "$action" != destroy ]; then
    to=$(grep -hoE '^# lodge-import: [A-Za-z0-9_.]+' "$module_dir"/*.tf | head -1 | sed 's/^# lodge-import: //')
    [ -n "$to" ] || { echo "module '$module' declares no '# lodge-import:' address" >&2; exit 1; }
    printf 'import {\n  to = module.unit.%s\n  id = %s\n}\n' "$to" "$(jq -n --arg id "$import_id" '$id')" > "$root/import.tf"
fi

echo "== $action unit '$unit' (module $module, workspace $workspace)"
cat "$root/unit.tf.json"; echo
[ -f "$root/import.tf" ] && cat "$root/import.tf"

# --- run ---------------------------------------------------------------------------------
if [ "$action" = validate ]; then
    cd "$root"
    terraform init -input=false -no-color -backend=false >/dev/null
    terraform validate -no-color
    exit 0
fi

if [ "$uses_proxmox" = 1 ]; then
    lodge_ssh_setup
    eval "$(ssh-agent -s)" >/dev/null
    trap 'ssh-agent -k >/dev/null 2>&1 || true' EXIT
    ssh-add -q /root/.ssh/id_lodge
fi

case "$action" in
    apply|destroy|plan) ;;
    *) echo "unknown action '$action' (expected apply, destroy, plan or validate)" >&2; exit 2 ;;
esac

cd "$root"
export TF_IN_AUTOMATION=1
env_file=$(mktemp)
LODGE_TF_VARS=1 lodge_pass_env_file "$env_file" $(lodge_pass_markers "$root"/*.tf "$root/modules/$module"/*.tf)

# Everything from `init` on needs the backend's connection string: one pass-cli run.
lodge_with_secrets "$env_file" /usr/local/lib/lodge/tf-run.sh "$action" "$workspace"
