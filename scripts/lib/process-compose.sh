#!/bin/bash
# Checks that process-compose (https://github.com/F1bonacc1/process-compose) is on PATH.
# Never installs it — see the README's "Prerequisites".

lodge_ensure_process_compose() {
    # Its official installer's suggested location, which may not be on PATH yet.
    if ! command -v process-compose >/dev/null 2>&1 && [ -x "$HOME/.local/bin/process-compose" ]; then
        export PATH="$HOME/.local/bin:$PATH"
    fi

    if ! command -v process-compose >/dev/null 2>&1; then
        echo "error: process-compose not found on PATH (see README.md, Prerequisites)." >&2
        return 1
    fi

    # Without its (empty is fine) config dir, every process-compose invocation prints a
    # "config home not found" debug line to stderr before the TUI starts.
    mkdir -p "${XDG_CONFIG_HOME:-$HOME/.config}/process-compose"
}
