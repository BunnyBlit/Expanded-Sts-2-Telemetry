#!/usr/bin/env bash
set -e

SRC="$HOME/Library/Application Support/SlayTheSpire2/logs/godot.log"
DEST="$(cd "$(dirname "$0")" && pwd)/logs/godot.log"

mkdir -p "$(dirname "$DEST")"
cp "$SRC" "$DEST"
echo "Copied godot.log to $DEST"
