#!/usr/bin/env bash
# Extract Slay the Spire 2's packed Godot assets (the ~1.9GB game .pck) using GDRE Tools
# (gdsdecomp, https://github.com/GDRETools/gdsdecomp). This is the Godot-asset counterpart
# to decompile.sh (which handles the C# sts2.dll).
#
# Modes:
#   scripts/extract-assets.sh [gdre args...]            # extract raw files  -> extracted_assets/
#   scripts/extract-assets.sh --recover [gdre args...]  # full project recovery -> recovered_project/
#     (recover also decompiles GDScript .gdc -> .gd and converts .scn/.res -> .tscn/.tres)
#   scripts/extract-assets.sh --clean ...               # wipe the output dir first
#
# A bare run extracts ALL 12k+ files (large + slow). Narrow it with a glob, e.g.:
#   scripts/extract-assets.sh --include=res://**/*.png
#
# Requires the GDRE Tools binary. Point GDRE_TOOLS_PATH at it (binary or the .app bundle),
# or install it so this script can find it on PATH / in /Applications. The modding MCP's
# `python -m sts2mcp.setup` downloads it automatically.
#
# Output dirs are gitignored (large, game-version-specific). Regenerate after a game update.
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"

# ---- parse leading flags; everything else passes through to gdre ----------------------
MODE="extract"
OUT_NAME="extracted_assets"
CLEAN=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --recover) MODE="recover"; OUT_NAME="recovered_project"; shift ;;
    --clean)   CLEAN=1; shift ;;
    --)        shift; break ;;
    *)         break ;;
  esac
done

# ---- STS2_DIR + locate the game .pck --------------------------------------------------
if [[ -z "${STS2_DIR:-}" ]]; then
  echo "error: STS2_DIR is not set. Export it to the folder containing sts2.dll (see README)." >&2
  exit 1
fi

find_pck() {
  # Explicit override wins.
  if [[ -n "${STS2_PCK:-}" ]]; then
    [[ -f "$STS2_PCK" ]] && { printf '%s\n' "$STS2_PCK"; return 0; }
    echo "error: STS2_PCK is set but not a file: $STS2_PCK" >&2; return 1
  fi
  # macOS: .pck sits in Contents/Resources (one up from data_sts2_macos_arm64).
  # Win/Linux: near the game root (one or two up from the Managed/data dir).
  local dirs=("$STS2_DIR" "$STS2_DIR/.." "$STS2_DIR/../..")
  local names=("Slay the Spire 2.pck" "SlayTheSpire2.pck" "game.pck" "data.pck")
  local d dd n f
  for d in "${dirs[@]}"; do
    [[ -d "$d" ]] || continue
    dd="$(cd "$d" && pwd)"
    for n in "${names[@]}"; do
      [[ -f "$dd/$n" ]] && { printf '%s\n' "$dd/$n"; return 0; }
    done
  done
  # Fall back to the first *.pck found in those dirs.
  for d in "${dirs[@]}"; do
    [[ -d "$d" ]] || continue
    dd="$(cd "$d" && pwd)"
    for f in "$dd"/*.pck; do
      [[ -f "$f" ]] && { printf '%s\n' "$f"; return 0; }
    done
  done
  return 1
}

PCK="$(find_pck || true)"
if [[ -z "$PCK" ]]; then
  echo "error: could not find the game .pck near STS2_DIR." >&2
  echo "       Set STS2_PCK to its full path (e.g. '.../Contents/Resources/Slay the Spire 2.pck')." >&2
  exit 1
fi

# ---- locate the GDRE Tools binary -----------------------------------------------------
find_gdre() {
  local cand
  # 1. GDRE_TOOLS_PATH — accept either the binary or a .app bundle.
  if [[ -n "${GDRE_TOOLS_PATH:-}" ]]; then
    if [[ -x "$GDRE_TOOLS_PATH" && -f "$GDRE_TOOLS_PATH" ]]; then
      printf '%s\n' "$GDRE_TOOLS_PATH"; return 0
    fi
    if [[ -d "$GDRE_TOOLS_PATH" ]]; then
      for cand in "$GDRE_TOOLS_PATH/Contents/MacOS/Godot RE Tools" "$GDRE_TOOLS_PATH/Contents/MacOS/GDRE_tools"; do
        [[ -x "$cand" ]] && { printf '%s\n' "$cand"; return 0; }
      done
    fi
  fi
  # 2. PATH.
  local n
  for n in gdre_tools gdre_tools.x86_64 "Godot RE Tools"; do
    cand="$(command -v "$n" 2>/dev/null || true)"
    [[ -n "$cand" ]] && { printf '%s\n' "$cand"; return 0; }
  done
  # 3. macOS app bundles in standard install locations.
  if [[ "$(uname)" == "Darwin" ]]; then
    local base app bin
    for base in "/Applications" "$HOME/Applications"; do
      for app in "Godot RE Tools.app" "GDRE_tools.app"; do
        for bin in "Godot RE Tools" "GDRE_tools"; do
          cand="$base/$app/Contents/MacOS/$bin"
          [[ -x "$cand" ]] && { printf '%s\n' "$cand"; return 0; }
        done
      done
    done
  fi
  return 1
}

GDRE="$(find_gdre || true)"
if [[ -z "$GDRE" ]]; then
  echo "error: GDRE Tools not found." >&2
  echo "       Set GDRE_TOOLS_PATH to the binary (or its .app bundle), or install it from" >&2
  echo "       https://github.com/GDRETools/gdsdecomp/releases" >&2
  echo "       (the modding MCP's 'python -m sts2mcp.setup' downloads it automatically)." >&2
  exit 1
fi

# ---- run ------------------------------------------------------------------------------
OUT_DIR="$PROJECT_DIR/$OUT_NAME"
if [[ "$CLEAN" == "1" ]]; then
  rm -rf "$OUT_DIR"
fi
mkdir -p "$OUT_DIR"

echo "GDRE Tools : $GDRE"
echo "PCK        : $PCK"
echo "Mode       : $MODE"
echo "Output     : $OUT_DIR"
[[ $# -gt 0 ]] && echo "Extra args : $*"
echo "(a full extract is ~12k files / gigabytes and can take a while)"

if [[ "$MODE" == "recover" ]]; then
  "$GDRE" --headless --recover="$PCK" --output="$OUT_DIR" "$@"
else
  "$GDRE" --headless --extract="$PCK" --output="$OUT_DIR" "$@"
fi

echo "Done. Assets are in $OUT_NAME/ (gitignored)."
