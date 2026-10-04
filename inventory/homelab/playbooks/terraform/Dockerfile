# Generic Terraform runner for the homelab: each run applies ONE module of modules/ as its
# own root, with its own state (see run.sh). Debian, not the alpine terraform image:
# pass-cli is linked against glibc.
# Base images are pinned by digest: a rebuild (new host, pruned cache, another runtime)
# then produces the image that was approved, and bumping one is a reviewed change.
FROM debian:bookworm-slim@sha256:3783cc01769c7b2b1b83a5c5ad96c815348e28ed7da68e2e3687004faa906251

ARG TERRAFORM_VERSION=1.16.5
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl unzip openssh-client jq postgresql-client \
    && rm -rf /var/lib/apt/lists/* \
    && curl -fsSLo /tmp/terraform.zip \
        "https://releases.hashicorp.com/terraform/${TERRAFORM_VERSION}/terraform_${TERRAFORM_VERSION}_linux_amd64.zip" \
    && unzip /tmp/terraform.zip terraform -d /usr/local/bin \
    && rm /tmp/terraform.zip

# pass-cli, pinned once for every playbook in the shared `base` context (playbooks/_base).
RUN --mount=from=base,target=/base /base/install-pass-cli.sh

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

COPY --from=base --chmod=0755 lodge-lib.sh /usr/local/lib/lodge/
COPY --chmod=0755 run.sh tf-run.sh /usr/local/lib/lodge/
ENTRYPOINT ["/usr/local/lib/lodge/run.sh"]
