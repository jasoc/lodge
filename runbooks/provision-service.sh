#!/bin/sh
# Example AUTO runbook for the `acme` demo kind: pretends to provision a service.
# Real runbooks would call whatever actually needs to happen (a script, an API, ansible-
# playbook, terraform apply, ...) — Lodge only cares about the exit code.
set -eu
echo "provisioning service '${LODGE_PARAM_SERVICE_KEY:-unknown}' for instance '${LODGE_INSTANCE_CODE:-unknown}'"
mkdir -p data/runbook-marks
touch "data/runbook-marks/${LODGE_INSTANCE_CODE:-unknown}-${LODGE_PARAM_SERVICE_KEY:-unknown}.provisioned"
echo "done"
