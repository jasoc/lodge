#!/bin/sh
# Shared by every homelab playbook image (one copy per build context — each folder is its
# own image). Mirrors the conventions of the homelab repo's scripts:
#  - secrets: "pass: VAR" markers resolve to pass://$PASS_PREFIX/VAR through pass-cli
#    (installed in the image). It logs in with a Proton Pass personal access token — the
#    `pass_pat` secret input, the one thing Lodge hands over — into a fresh session dir
#    that dies with the container; no host session or file is ever mounted;
#  - ssh: one private key for the VMs as $SSH_USER and for Proxmox, fetched from Proton
#    Pass too: pass://$PASS_PREFIX/$SSH_KEY_VAR, base64 (or a plain PEM).

PASS_PREFIX="${PASS_PREFIX:-Homelab/environments}"
SSH_USER="${SSH_USER:-parisius}"
SSH_KEY_VAR="${SSH_KEY_VAR:-SSH_PRIVATE_KEY_B64}"
LODGE_PASS_LOGGED_IN=0

# lodge_pass_env_file <out-file> <var>...  — one VAR="pass://..." line per name.
# With LODGE_TF_VARS=1 each also gets a TF_VAR_<lowercase> twin (like terraform.sh).
lodge_pass_env_file() {
    out="$1"; shift
    : > "$out"
    for var in "$@"; do
        [ -n "$var" ] || continue
        echo "${var}=\"pass://${PASS_PREFIX}/${var}\"" >> "$out"
        if [ "${LODGE_TF_VARS:-0}" = 1 ]; then
            echo "TF_VAR_$(echo "$var" | tr '[:upper:]' '[:lower:]')=\"pass://${PASS_PREFIX}/${var}\"" >> "$out"
        fi
    done
}

# lodge_pass_markers <file>... — the VAR names of every "# pass: VAR" line.
lodge_pass_markers() {
    grep -hoE '^# pass: [A-Za-z_][A-Za-z0-9_]*$' "$@" 2>/dev/null | sed -E 's/^# pass: //' | sort -u
}

# lodge_pass_login — an ephemeral pass-cli session for this run, from the PAT input. Once.
lodge_pass_login() {
    [ "$LODGE_PASS_LOGGED_IN" = 1 ] && return 0
    [ -n "${LODGE_PARAM_PASS_PAT:-}" ] || {
        echo "no Proton Pass token: the action needs a 'pass_pat: { secret: PROTON_PASS_PAT }' input" >&2
        exit 1
    }
    PROTON_PASS_SESSION_DIR="$(mktemp -d)"
    export PROTON_PASS_SESSION_DIR PROTON_PASS_KEY_PROVIDER=fs PROTON_PASS_NO_UPDATE_CHECK=1 PROTON_PASS_DISABLE_TELEMETRY=1
    PROTON_PASS_PERSONAL_ACCESS_TOKEN="$LODGE_PARAM_PASS_PAT" pass-cli login --pat "$LODGE_PARAM_PASS_PAT" >/dev/null
    LODGE_PASS_LOGGED_IN=1
}

# lodge_pass_value <VAR> — prints pass://$PASS_PREFIX/VAR (for values a script needs as a
# file rather than as an env var, like the ssh key).
lodge_pass_value() {
    lodge_pass_login
    value_env=$(mktemp)
    LODGE_TF_VARS=0 lodge_pass_env_file "$value_env" "$1"
    pass-cli run --env-file "$value_env" --no-masking -- printenv "$1"
    rm -f "$value_env"
}

# lodge_with_secrets <env-file> <cmd>... — runs cmd under pass-cli when there's anything to resolve.
lodge_with_secrets() {
    env_file="$1"; shift
    if [ -s "$env_file" ]; then
        lodge_pass_login
        echo ">>> pass-cli run -- $*"
        pass-cli run --env-file "$env_file" --no-masking -- "$@"
    else
        echo ">>> $*"
        "$@"
    fi
}

# lodge_ssh_setup — writes the key from Proton Pass with the 0600 mode ssh insists on, and
# trusts hosts without prompting (homelab VMs are rebuilt/re-IP'd often, like ansible.cfg).
lodge_ssh_setup() {
    lodge_pass_login   # here, not inside $(...) below: the session must outlive a subshell
    mkdir -p /root/.ssh
    umask 077
    key=$(lodge_pass_value "$SSH_KEY_VAR")
    [ -n "$key" ] || { echo "pass://${PASS_PREFIX}/${SSH_KEY_VAR} is empty — store the ssh private key there (base64)" >&2; exit 1; }
    case "$key" in
        -----BEGIN*) printf '%s\n' "$key" > /root/.ssh/id_lodge ;;
        *) printf '%s' "$key" | base64 -d > /root/.ssh/id_lodge ;;
    esac
    umask 022
    chmod 0600 /root/.ssh/id_lodge
    cat > /root/.ssh/config <<CFG
Host *
    User ${SSH_USER}
    IdentityFile /root/.ssh/id_lodge
    IdentitiesOnly yes
    StrictHostKeyChecking no
    UserKnownHostsFile /dev/null
    LogLevel ERROR
CFG
    chmod 0600 /root/.ssh/config
}

# lodge_host_ip <cidr-or-ip> — "192.168.178.112/24" -> "192.168.178.112".
lodge_host_ip() {
    echo "${1%%/*}"
}
