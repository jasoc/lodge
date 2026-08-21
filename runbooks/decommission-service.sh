#!/bin/sh
# Example MANUAL_REQUIRED runbook for the `acme` demo kind: pretends to tear a
# service down. Gated behind human confirmation, unlike provision-service.sh.
set -eu
echo "decommissioning service '${LODGE_PARAM_SERVICE_KEY:-unknown}' for instance '${LODGE_INSTANCE_CODE:-unknown}'"
rm -f "data/runbook-marks/${LODGE_INSTANCE_CODE:-unknown}-${LODGE_PARAM_SERVICE_KEY:-unknown}.provisioned"
echo "done"
