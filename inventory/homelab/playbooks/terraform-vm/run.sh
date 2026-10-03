#!/bin/sh
# Usage: run.sh create|destroy
# Lodge passes the VM as LODGE_PARAM_VM_NAME and LODGE_PARAM_SPEC (the VM's whole entry
# as JSON); Terraform reads them straight from TF_VAR_* — a JSON object is valid HCL.
set -eu

action="${1:?usage: run.sh create|destroy}"
export TF_VAR_vm_name="${LODGE_PARAM_VM_NAME:?missing vm_name input}"
export TF_VAR_vm="${LODGE_PARAM_SPEC:?missing spec input}"
export TF_IN_AUTOMATION=1

echo "== $action VM '$TF_VAR_vm_name' (instance $LODGE_INSTANCE_CODE, action $LODGE_ACTION_ID)"
case "$action" in
    create)
        terraform apply -auto-approve -input=false -no-color
        ;;
    destroy)
        # Pretend: state isn't persisted between runs here, so rebuild it from the last
        # known spec first, then destroy it. A real setup keeps state in a remote backend.
        terraform apply -auto-approve -input=false -no-color >/dev/null
        terraform destroy -auto-approve -input=false -no-color
        ;;
    *)
        echo "unknown action '$action' (expected create or destroy)" >&2
        exit 2
        ;;
esac
