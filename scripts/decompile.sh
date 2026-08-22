#!/usr/bin/env bash
# Decompile the locally-installed Slay the Spire 2 assembly (sts2.dll) into decompiled/
# for code reference. Uses ilspycmd in project mode, which emits a namespace-folder
# layout plus an sts2.csproj (the same structure the modding MCP produces).
#
# decompiled/ is gitignored — it's a large, game-version-specific artifact. Regenerate
# it after each game update (Steam auto-updates), then diff to see what the patch changed.
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"

if [[ -z "${STS2_DIR:-}" ]]; then
  echo "error: STS2_DIR is not set. Export it to the folder containing sts2.dll (see README)." >&2
  exit 1
fi

DLL="$STS2_DIR/sts2.dll"
if [[ ! -f "$DLL" ]]; then
  echo "error: sts2.dll not found at: $DLL" >&2
  echo "       Check that STS2_DIR points at the folder containing sts2.dll." >&2
  exit 1
fi

# Resolve ilspycmd: prefer PATH, fall back to the default global-tool install location
# (~/.dotnet/tools is often not on a non-interactive shell's PATH).
if command -v ilspycmd >/dev/null 2>&1; then
  ILSPYCMD="ilspycmd"
elif [[ -x "$HOME/.dotnet/tools/ilspycmd" ]]; then
  ILSPYCMD="$HOME/.dotnet/tools/ilspycmd"
else
  echo "error: ilspycmd not found. Install it with:" >&2
  echo "    dotnet tool install -g ilspycmd" >&2
  echo "(this script also looks in ~/.dotnet/tools if it isn't on your PATH)." >&2
  exit 1
fi

OUT_DIR="$PROJECT_DIR/decompiled"

echo "Decompiling: $DLL"
echo "        -> : $OUT_DIR"
echo "Using ilspycmd ($("$ILSPYCMD" --version 2>/dev/null | head -1)); this takes a minute for the full assembly..."

# Start clean so a type removed/renamed in a game update can't linger as a stale file.
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

# --project: compilable project layout (folders by namespace + sts2.csproj).
# References are resolved against sts2.dll's own directory, so run it against STS2_DIR in place.
"$ILSPYCMD" --project --outputdir "$OUT_DIR" "$DLL"

echo "Done. Decompiled source is in decompiled/ (gitignored)."
