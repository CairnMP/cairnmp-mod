#!/usr/bin/env bash
# scripts/check.sh — full local checkup before pushing (mod repo).
# Works with bash (git-bash on Windows, native Linux/Mac).
#
# Usage: bash scripts/check.sh
# Run it manually — no git hook invokes it automatically.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

step() { printf "\n\033[1;36m[check]\033[0m %s\n" "$1"; }
ok()   { printf "\033[1;32m  OK\033[0m %s\n" "$1"; }
fail() { printf "\033[1;31m  FAIL\033[0m %s\n" "$1"; exit 1; }

START=$SECONDS

# ── Mod (.NET / xUnit) ───────────────────────────────────────────────────────
step "mod: dotnet test"
dotnet test CairnMultiplayer.slnx -c Release --nologo --verbosity minimal || fail "mod"
ok "mod"

ELAPSED=$((SECONDS - START))
printf "\n\033[1;32mAll checks passed\033[0m in %ds\n" "$ELAPSED"
