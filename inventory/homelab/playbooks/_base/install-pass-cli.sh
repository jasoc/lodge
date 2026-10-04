#!/bin/sh
# Installs pass-cli into /usr/local/bin, pinned and checksum-verified (from
# https://proton.me/download/pass-cli/versions.json). Every playbook image runs this from
# the shared `base` build context, so the pin lives here once: bumping it is a deliberate
# change to every playbook, and re-queues their pending actions.
# Needs curl and ca-certificates in the image.
set -eu

PASS_CLI_VERSION=2.4.2
PASS_CLI_SHA256=4089bdf5981140ac5bee65d2d79bf98767537f54d33197b79e5f86c630714842

curl -fsSLo /usr/local/bin/pass-cli \
    "https://proton.me/download/pass-cli/${PASS_CLI_VERSION}/pass-cli-linux-x86_64"
echo "${PASS_CLI_SHA256}  /usr/local/bin/pass-cli" | sha256sum -c -
chmod 0755 /usr/local/bin/pass-cli
