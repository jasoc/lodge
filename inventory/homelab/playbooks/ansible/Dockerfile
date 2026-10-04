# Ansible for the homelab VMs (ported from the homelab repo's ansible/). Debian-based
# python: pass-cli is linked against glibc.
# Base images are pinned by digest: a rebuild (new host, pruned cache, another runtime)
# then produces the image that was approved, and bumping one is a reviewed change.
FROM python:3.12-slim-bookworm@sha256:54c85f3c47607a77f32adec749d3c81d1348bf25833671f512b26a9b6d778cb3

ARG ANSIBLE_CORE_VERSION=2.18.6
ENV PIP_ROOT_USER_ACTION=ignore \
    PIP_NO_CACHE_DIR=1 \
    ANSIBLE_CONFIG=/ansible/ansible.cfg \
    ANSIBLE_PYTHON_INTERPRETER=auto_silent
RUN apt-get update \
    && apt-get install -y --no-install-recommends openssh-client curl \
    && rm -rf /var/lib/apt/lists/* \
    && pip install "ansible-core==${ANSIBLE_CORE_VERSION}"

# pass-cli, pinned once for every playbook in the shared `base` context (playbooks/_base).
RUN --mount=from=base,target=/base /base/install-pass-cli.sh

WORKDIR /ansible
COPY ansible.cfg ./
COPY roles/ roles/
COPY playbooks/ playbooks/
COPY --from=base --chmod=0755 lodge-lib.sh /usr/local/lib/lodge/
COPY --chmod=0755 run.sh /usr/local/lib/lodge/
ENTRYPOINT ["/usr/local/lib/lodge/run.sh"]
