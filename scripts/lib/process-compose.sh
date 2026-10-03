#!/bin/bash
# Makes sure process-compose (https://github.com/F1bonacc1/process-compose) is on PATH,
# installing it with the project's official installer into ~/.local/bin if missing.

lodge_ensure_process_compose() {
    # Without its (empty is fine) config dir, every process-compose invocation prints a
    # "config home not found" debug line to stderr before the TUI starts.
    mkdir -p "${XDG_CONFIG_HOME:-$HOME/.config}/process-compose"

    if [ -x "$HOME/.local/bin/process-compose" ]; then
        export PATH="$HOME/.local/bin:$PATH"
    fi

    if command -v process-compose >/dev/null 2>&1; then
        return 0
    fi

    echo "==> process-compose not found, installing it into ~/.local/bin"
    sh -c "$(curl --location https://raw.githubusercontent.com/F1bonacc1/process-compose/main/scripts/get-pc.sh)" \
        -- -d -b "$HOME/.local/bin"
    export PATH="$HOME/.local/bin:$PATH"
}
