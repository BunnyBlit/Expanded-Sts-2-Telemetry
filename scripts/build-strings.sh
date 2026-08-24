#!/usr/bin/env bash
# Reproducible display-name pipeline for the telemetry webapp.
#
# Builds a us-en rendering of the telemetry stream's ALL_CAPS ids (STRIKE_IRONCLAD -> "Strike",
# BURNING_BLOOD -> "Burning Blood", SILENT -> "Silent", ...) straight from the game's own
# localization. Writes strings_dist/en_us.json — a per-category { ID -> "Display Name" } map,
# keyed the same way as icons.json. Re-run after a game update (reads the live pck each time).
#
# Requires STS2_DIR (or STS2_PCK) + GDRE_TOOLS_PATH, same as the other asset scripts. Pure
# stdlib python3 — no venv needed. Any args pass through to the pipeline.
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
exec python3 "$PROJECT_DIR/scripts/strings_pipeline.py" "$@"
