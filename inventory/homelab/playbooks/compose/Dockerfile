# Deploys one compose file (inventory/homelab/stacks/) onto a homelab VM's docker daemon over
# ssh. Debian base (pass-cli is glibc-linked) with the static docker CLI and compose
# plugin taken from the official image.
# Base images are pinned by digest: a rebuild (new host, pruned cache, another runtime)
# then produces the image that was approved, and bumping one is a reviewed change.
FROM docker:28-cli@sha256:625d9431a9f54c5a2bc90f24f0e1c3d55b1349fd857dd85035f98c2c9acbdd4d AS docker-cli

FROM debian:bookworm-slim@sha256:3783cc01769c7b2b1b83a5c5ad96c815348e28ed7da68e2e3687004faa906251
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl openssh-client jq \
    && rm -rf /var/lib/apt/lists/*
COPY --from=docker-cli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=docker-cli /usr/local/libexec/docker/cli-plugins/docker-compose /usr/local/libexec/docker/cli-plugins/docker-compose

# pass-cli, pinned once for every playbook in the shared `base` context (playbooks/_base).
RUN --mount=from=base,target=/base /base/install-pass-cli.sh

WORKDIR /work
COPY --from=base --chmod=0755 lodge-lib.sh /usr/local/lib/lodge/
COPY --chmod=0755 run.sh /usr/local/lib/lodge/
ENTRYPOINT ["/usr/local/lib/lodge/run.sh"]
