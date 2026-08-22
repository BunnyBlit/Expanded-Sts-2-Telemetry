#!/usr/bin/env bash
# Compile only — no copy into the game, no packaging.
# Defaults to Debug; pass -c Release (or any dotnet args) to override.
set -e

PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"

dotnet build "$PROJECT_DIR/expanded-telemetry.csproj" -c Debug "$@"
