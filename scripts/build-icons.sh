#!/usr/bin/env bash
# Reproducible icon pipeline for the telemetry webapp.
#
# Extracts SlayTheSpire2's icon assets from the game .pck, normalizes each to a 64x64 PNG,
# and writes them plus a JSON atlas (icons.json) mapping each icon to its in-game telemetry
# strings. Output lands in the gitignored icons_dist/ for copying into the webapp's static/.
#
# Re-run after a game update to refresh the set (it reads the live pck each time).
#
# Requires:
#   STS2_DIR         — folder containing sts2.dll (used to locate the .pck; STS2_PCK overrides)
#   GDRE_TOOLS_PATH  — GDRE Tools binary or .app bundle (see scripts/extract-assets.sh)
#   python3          — a local venv with Pillow is provisioned automatically under .venv-icons/
#
# Any args (e.g. --clean) pass through to the pipeline.
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
VENV="$PROJECT_DIR/.venv-icons"

if [[ ! -x "$VENV/bin/python" ]]; then
  echo "Provisioning icon-pipeline venv (.venv-icons) with Pillow..."
  python3 -m venv "$VENV"
  "$VENV/bin/pip" install --quiet --upgrade pip
  "$VENV/bin/pip" install --quiet Pillow
fi

exec "$VENV/bin/python" "$PROJECT_DIR/scripts/icons_pipeline.py" "$@"
