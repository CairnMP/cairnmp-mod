#!/usr/bin/env bash
# scripts/check.sh — checkup local complet avant push (repo mod).
# Compatible bash (git-bash sous Windows, native Linux/Mac).
#
# Usage : bash scripts/check.sh
# A lancer manuellement avant un push.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

step() { printf "\n\033[1;36m[check]\033[0m %s\n" "$1"; }
ok()   { printf "\033[1;32m  OK\033[0m %s\n" "$1"; }
fail() { printf "\033[1;31m  FAIL\033[0m %s\n" "$1"; exit 1; }

START=$SECONDS

# ── Mod (.NET / xUnit) ───────────────────────────────────────────────────────
step "mod: dotnet test"
dotnet restore CairnMultiplayer.sln --locked-mode || fail "restore"
node scripts/sync-versions.js --check || fail "versions"
dotnet test CairnMultiplayer.sln -c Release --no-restore -p:DeployMod=false --nologo --verbosity minimal || fail "mod"
ok "mod"

ELAPSED=$((SECONDS - START))
printf "\n\033[1;32mAll checks passed\033[0m in %ds\n" "$ELAPSED"
