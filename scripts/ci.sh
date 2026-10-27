#!/usr/bin/env bash
# CI_CMD — script unique de la CI (contrat C11) : build, tests, audit des dependances.
# Appele par release.yml, ci-branch.yml, qa et deploy.
# En local WSL : DOTNET=/mnt/c/Users/cyril/AppData/Local/Microsoft/dotnet/dotnet.exe scripts/ci.sh
set -euo pipefail
DOTNET="${DOTNET:-dotnet}"
cd "$(dirname "$0")/.."

"$DOTNET" build src/EmbySharedPlaylist/EmbySharedPlaylist.csproj --configuration Release --output dist/
"$DOTNET" test tests/EmbySharedPlaylist.Tests/ --configuration Release
AUDIT=$("$DOTNET" list src/EmbySharedPlaylist package --vulnerable)
echo "$AUDIT"
# Une ligne "> <package>" par paquet vulnerable — repere independant de la langue du CLI
if grep -qE '^\s*>\s' <<<"$AUDIT"; then
  echo "Dependances vulnerables detectees" >&2
  exit 1
fi
