#!/bin/bash
# Makes sure a .NET SDK compatible with the repo's global.json is on PATH, installing the
# pinned version into ~/.dotnet if nothing suitable is found. Trusts global.json's own
# rollForward resolution rather than re-implementing version comparison here.

lodge_ensure_dotnet() {
    local root="$1"

    if [ -x "$HOME/.dotnet/dotnet" ]; then
        export PATH="$HOME/.dotnet:$PATH"
    fi

    if command -v dotnet >/dev/null 2>&1 && (cd "$root" && dotnet --version >/dev/null 2>&1); then
        echo "==> Using dotnet $(cd "$root" && dotnet --version) (resolved via global.json)"
        return 0
    fi

    local pinned
    pinned=$(grep -o '"version": *"[^"]*"' "$root/global.json" | head -1 | sed -E 's/.*"([^"]+)"$/\1/')
    echo "==> No compatible .NET SDK found, installing $pinned into ~/.dotnet"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/lodge-dotnet-install.sh
    bash /tmp/lodge-dotnet-install.sh --version "$pinned" --install-dir "$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
}
