#!/bin/bash
# Checks that the Node version pinned in ui/.nvmrc is the one on PATH. Never installs
# anything — see the README's "Prerequisites". If nvm is present it's used to switch to the
# pinned version (which must already be installed); otherwise whatever `node` is on PATH
# (fnm, mise, volta, a distro package, ...) must already be exactly that version.

lodge_ensure_node() {
    local spa_dir="$1"
    local pinned
    pinned=$(tr -d '[:space:]' < "$spa_dir/.nvmrc")
    pinned="${pinned#v}"

    export NVM_DIR="${NVM_DIR:-$HOME/.nvm}"
    if [ -s "$NVM_DIR/nvm.sh" ]; then
        # shellcheck disable=SC1091
        \. "$NVM_DIR/nvm.sh" --no-use
        if ! nvm use "$pinned" >/dev/null 2>&1; then
            echo "error: Node $pinned (ui/.nvmrc) is not installed in nvm. Run: nvm install $pinned" >&2
            return 1
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
