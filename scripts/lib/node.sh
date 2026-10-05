#!/bin/bash
# Makes sure the Node version pinned in ui/.nvmrc is the one on PATH. If nvm is present it's
# used to switch to the pinned version, installing it first when missing (the one tool the
# scripts install on their own); otherwise whatever `node` is on PATH (fnm, mise, volta, a
# distro package, ...) must already be exactly that version — see the README's "Prerequisites".

lodge_ensure_node() {
    local spa_dir="$1"
    local pinned
    pinned=$(tr -d '[:space:]' < "$spa_dir/.nvmrc")
    pinned="${pinned#v}"

    export NVM_DIR="${NVM_DIR:-$HOME/.nvm}"
    local nvm_sh=""
    if [ -s "$NVM_DIR/nvm.sh" ]; then
        nvm_sh="$NVM_DIR/nvm.sh"
    elif [ -s /usr/share/nvm/nvm.sh ]; then
        # Distro package (e.g. Arch `nvm`): nvm.sh lives under /usr/share/nvm, not $NVM_DIR
        nvm_sh=/usr/share/nvm/nvm.sh
    fi
    if [ -n "$nvm_sh" ]; then
        # shellcheck disable=SC1090
        \. "$nvm_sh" --no-use
        if ! nvm use "$pinned" >/dev/null 2>&1; then
            echo "==> Installing Node $pinned via nvm"
            if ! nvm install "$pinned" || ! nvm use "$pinned" >/dev/null 2>&1; then
                echo "error: could not install Node $pinned (ui/.nvmrc) with nvm." >&2
                return 1
            fi
        fi
    fi

    if ! command -v node >/dev/null 2>&1; then
        echo "error: node not found on PATH. Install Node $pinned (see README.md, Prerequisites)." >&2
        return 1
    fi

    local current
    current=$(node --version)
    if [ "${current#v}" != "$pinned" ]; then
        echo "error: node on PATH is $current, but ui/.nvmrc pins $pinned." >&2
        echo "       Switch to it with your version manager (e.g. nvm install $pinned)." >&2
        return 1
    fi
    echo "==> Using node $current (ui/.nvmrc pins $pinned)"
}
