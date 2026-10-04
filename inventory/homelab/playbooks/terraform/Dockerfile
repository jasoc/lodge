# Generic Terraform runner for the homelab: each run applies ONE module of modules/ as its
# own root, with its own state (see run.sh). Debian, not the alpine terraform image:
# pass-cli is linked against glibc.
FROM debian:bookworm-slim

ARG TERRAFORM_VERSION=1.16.5
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl unzip openssh-client jq \
    && rm -rf /var/lib/apt/lists/* \
    && curl -fsSLo /tmp/terraform.zip \
        "https://releases.hashicorp.com/terraform/${TERRAFORM_VERSION}/terraform_${TERRAFORM_VERSION}_linux_amd64.zip" \
    && unzip /tmp/terraform.zip terraform -d /usr/local/bin \
    && rm /tmp/terraform.zip

# pass-cli, pinned and checksum-verified (from https://proton.me/download/pass-cli/versions.json):
# bumping it is a deliberate change to this playbook, so it re-queues pending actions.
ARG PASS_CLI_VERSION=2.4.2
ARG PASS_CLI_SHA256=4089bdf5981140ac5bee65d2d79bf98767537f54d33197b79e5f86c630714842
RUN curl -fsSLo /usr/local/bin/pass-cli \
        "https://proton.me/download/pass-cli/${PASS_CLI_VERSION}/pass-cli-linux-x86_64" \
    && echo "${PASS_CLI_SHA256}  /usr/local/bin/pass-cli" | sha256sum -c - \
    && chmod 0755 /usr/local/bin/pass-cli

WORKDIR /tf
COPY root.tf versions.tf ./
COPY providers/ providers/
COPY modules/ modules/

# Every provider downloaded once into a shared cache, plus the lock file each generated
# root starts from — no registry round-trip per run.
ENV TF_PLUGIN_CACHE_DIR=/opt/terraform/plugin-cache
RUN mkdir -p "$TF_PLUGIN_CACHE_DIR" /tmp/warm \
    && cp root.tf versions.tf /tmp/warm/ \
    && cd /tmp/warm && terraform init -backend=false -input=false -no-color \
    && cp .terraform.lock.hcl /tf/.terraform.lock.hcl \
    && rm -rf /tmp/warm

COPY --chmod=0755 lodge-lib.sh run.sh /usr/local/lib/lodge/
ENTRYPOINT ["/usr/local/lib/lodge/run.sh"]
