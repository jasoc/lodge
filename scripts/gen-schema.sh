#!/bin/bash
# Regenerates schemas/capability.schema.json and schemas/kind.schema.json from the catalog
# loader's own document classes (server/src/Lodge.Core/Catalog/Documents), through
# server/tools/gen-schema. The schemas are for editor support (YAML completion and typo
# checks); `lodge validate` keeps using the loader for the semantic errors. CI runs this and
# fails if it changes anything under schemas/, so a schema can't drift from the code.
# Requires the .NET SDK pinned in global.json; installs nothing.
# Usage: ./scripts/gen-schema.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/dotnet.sh
source "$ROOT/scripts/lib/dotnet.sh"

lodge_ensure_dotnet "$ROOT"

cd "$ROOT"
dotnet run --project server/tools/gen-schema -- schemas
