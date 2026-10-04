#!/bin/bash
# Runs the `lodge` CLI from the sources, as part of the local environment: it loads the repo's
# .env (like the other dev scripts) and uses the pinned .NET SDK, so you don't have to publish
# or install the binary. Arguments go straight to the CLI, and the working directory is yours,
# so relative paths work:
#   ./scripts/lodge-cli.sh login http://localhost:8080
#   ./scripts/lodge-cli.sh actions list homelab lab
#   ./scripts/lodge-cli.sh validate inventory
# Requires the .NET SDK pinned in global.json; installs nothing.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/env.sh
source "$ROOT/scripts/lib/env.sh"
# shellcheck source=lib/dotnet.sh
source "$ROOT/scripts/lib/dotnet.sh"

lodge_load_env "$ROOT" >&2
lodge_ensure_dotnet "$ROOT" >&2

exec dotnet run --verbosity quiet --project "$ROOT/cli/src/Lodge.Cli" -- "$@"
