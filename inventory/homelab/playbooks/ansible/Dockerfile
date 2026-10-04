# Ansible for the homelab VMs (ported from the homelab repo's ansible/). Debian-based
# python: pass-cli is linked against glibc.
FROM python:3.12-slim-bookworm

ARG ANSIBLE_CORE_VERSION=2.18.6
ENV PIP_ROOT_USER_ACTION=ignore \
    PIP_NO_CACHE_DIR=1 \
    ANSIBLE_CONFIG=/ansible/ansible.cfg \
    ANSIBLE_PYTHON_INTERPRETER=auto_silent
RUN apt-get update \
    && apt-get install -y --no-install-recommends openssh-client curl \
    && rm -rf /var/lib/apt/lists/* \
    && pip install "ansible-core==${ANSIBLE_CORE_VERSION}"

# pass-cli, pinned and checksum-verified (from https://proton.me/download/pass-cli/versions.json):
# bumping it is a deliberate change to this playbook, so it re-queues pending actions.
ARG PASS_CLI_VERSION=2.4.2
ARG PASS_CLI_SHA256=4089bdf5981140ac5bee65d2d79bf98767537f54d33197b79e5f86c630714842
RUN curl -fsSLo /usr/local/bin/pass-cli \
        "https://proton.me/download/pass-cli/${PASS_CLI_VERSION}/pass-cli-linux-x86_64" \
    && echo "${PASS_CLI_SHA256}  /usr/local/bin/pass-cli" | sha256sum -c - \
    && chmod 0755 /usr/local/bin/pass-cli

WORKDIR /ansible
COPY ansible.cfg ./
COPY roles/ roles/
COPY playbooks/ playbooks/
COPY --chmod=0755 lodge-lib.sh run.sh /usr/local/lib/lodge/
ENTRYPOINT ["/usr/local/lib/lodge/run.sh"]
