#!/bin/bash
# Makes sure nvm and the Node version pinned in ui/.nvmrc are available, installing
# both if missing, so `./scripts/run.sh` works on a machine that has neither.

lodge_ensure_node() {
    local spa_dir="$1"
    local pinned
    pinned=$(tr -d '[:space:]' < "$spa_dir/.nvmrc")

    export NVM_DIR="${NVM_DIR:-$HOME/.nvm}"
    if [ ! -s "$NVM_DIR/nvm.sh" ]; then
        echo "==> nvm not found, installing into $NVM_DIR"
        curl -fsSL https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.1/install.sh | bash
    fi

    # shellcheck disable=SC1091
    \. "$NVM_DIR/nvm.sh"

    if ! nvm ls "$pinned" >/dev/null 2>&1; then
        echo "==> Installing pinned Node $pinned via nvm"
        nvm install "$pinned"
    fi
    nvm use "$pinned" >/dev/null
    echo "==> Using node $(node --version) (nvm)"
}
