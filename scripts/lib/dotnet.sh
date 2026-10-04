#!/bin/bash
# Checks that a .NET SDK satisfying the repo's global.json is on PATH. Never installs
# anything — see the README's "Prerequisites" for how to get the pinned SDK. Trusts
# global.json's own rollForward resolution (the dotnet host picks the SDK and fails if none
# matches) rather than re-implementing version comparison here.

lodge_ensure_dotnet() {
    local root="$1"
    local pinned
    pinned=$(grep -o '"version": *"[^"]*"' "$root/global.json" | head -1 | sed -E 's/.*"([^"]+)"$/\1/')

    # dotnet-install.sh's default location, which it doesn't put on PATH by itself.
    if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
        export PATH="$HOME/.dotnet:$PATH"
        export DOTNET_ROOT="$HOME/.dotnet"
    fi

    if ! command -v dotnet >/dev/null 2>&1; then
        echo "error: dotnet not found on PATH. Install the .NET SDK $pinned (see README.md, Prerequisites)." >&2
        return 1
    fi

    local resolved
    if ! resolved=$(cd "$root" && dotnet --version 2>/dev/null); then
        echo "error: no installed .NET SDK satisfies global.json (pinned $pinned)." >&2
        echo "       Installed: $(dotnet --list-sdks 2>/dev/null | cut -d' ' -f1 | tr '\n' ' ')" >&2
        echo "       Install the pinned SDK (see README.md, Prerequisites)." >&2
        return 1
    fi
    echo "==> Using dotnet $resolved (global.json pins $pinned)"
}
