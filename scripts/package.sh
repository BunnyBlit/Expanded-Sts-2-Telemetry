#!/usr/bin/env bash
# Release packaging: fresh Release build, then zip a distributable artifact to
# dist/expanded-telemetry-<version>.zip (version read from mod_manifest.json).
# Upload the zip to ModsNexus or attach it to a GitHub release.
# Runs the MSBuild `Package` target (DependsOnTargets=Build). Requires STS2_DIR.
set -e

PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"

dotnet build "$PROJECT_DIR/expanded-telemetry.csproj" -c Release -t:Package "$@"
