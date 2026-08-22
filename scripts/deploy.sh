#!/usr/bin/env bash
# Local install: fresh Debug build, then copy the mod payload into the game's
# mods/expanded-telemetry/ folder. Runs the MSBuild `Deploy` target (DependsOnTargets=Build).
# Requires STS2_DIR. Pass extra dotnet args through (e.g. -c Release).
set -e

PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"

dotnet build "$PROJECT_DIR/expanded-telemetry.csproj" -c Debug -t:Deploy "$@"
