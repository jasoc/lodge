# Deploys one compose file (inventory/homelab/stacks/) onto a homelab VM's docker daemon over
# ssh. Debian base (pass-cli is glibc-linked) with the static docker CLI and compose
# plugin taken from the official image.
FROM docker:28-cli AS docker-cli

FROM debian:bookworm-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl openssh-client jq \
    && rm -rf /var/lib/apt/lists/*
COPY --from=docker-cli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=docker-cli /usr/local/libexec/docker/cli-plugins/docker-compose /usr/local/libexec/docker/cli-plugins/docker-compose

# pass-cli, pinned and checksum-verified (from https://proton.me/download/pass-cli/versions.json):
# bumping it is a deliberate change to this playbook, so it re-queues pending actions.
ARG PASS_CLI_VERSION=2.4.2
ARG PASS_CLI_SHA256=4089bdf5981140ac5bee65d2d79bf98767537f54d33197b79e5f86c630714842
RUN curl -fsSLo /usr/local/bin/pass-cli \
        "https://proton.me/download/pass-cli/${PASS_CLI_VERSION}/pass-cli-linux-x86_64" \
    && echo "${PASS_CLI_SHA256}  /usr/local/bin/pass-cli" | sha256sum -c - \
    && chmod 0755 /usr/local/bin/pass-cli

WORKDIR /work
COPY --chmod=0755 lodge-lib.sh run.sh /usr/local/lib/lodge/
ENTRYPOINT ["/usr/local/lib/lodge/run.sh"]
