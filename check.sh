#!/usr/bin/env bash
# The one check to run before every commit: build, format check, then tests.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

dotnet build firstmate-telegram.slnx --nologo
dotnet format firstmate-telegram.slnx --verify-no-changes --no-restore
dotnet test firstmate-telegram.slnx --no-build --nologo

if command -v shellcheck >/dev/null 2>&1; then
  shellcheck install.sh check.sh
else
  echo "shellcheck is not installed; skipped the shell script lint."
fi
