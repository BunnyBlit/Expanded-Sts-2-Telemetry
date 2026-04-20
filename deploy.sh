#!/usr/bin/env bash
set -e

PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"

dotnet build "$PROJECT_DIR/expanded-telemetry.csproj" -c Debug "$@"
