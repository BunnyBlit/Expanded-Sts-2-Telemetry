#!/usr/bin/env python3
"""Reproducible icon pipeline for the telemetry webapp.

Extracts Slay the Spire 2's icon assets from the packed game .pck, normalizes each to a
64x64 PNG, and writes them plus a JSON atlas (icons.json) that maps each icon to the
in-game telemetry strings it represents (e.g. potions -> potion_use.potion / reward item).

Run via scripts/build-icons.sh (which provisions the Pillow venv). Re-run after a game
update to refresh the set — the pipeline reads the live pck each time, so it tracks the
game's versions. Output lands in a gitignored icons_dist/ for copying into the webapp's
static/ folder.

Two extraction paths:
  * "dir"   sets   — per-icon source PNGs under a res:// folder (relics, potions, ...).
  * "atlas" sets   — sprites packed into an atlas sheet; sliced using the atlas .tpsheet
                     (a JSON manifest of every sprite's page + region rect).

Both are fed by a single GDRE Tools --recover pass. GDRE stores textures as VRAM-compressed
.ctex under res://.godot/imported/, so for every logical PNG we must also include its .ctex
in the recover set (a logical-path-only include yields just the .import sidecar, no pixels).
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys
from pathlib import Path

try:
    from PIL import Image
except ImportError:
    sys.exit("Pillow is required. Run this via scripts/build-icons.sh, which provisions it.")

ICON_SIZE = 64
PROJECT_ROOT = Path(__file__).resolve().parent.parent
OUTPUT_DIR = PROJECT_ROOT / "icons_dist"

# ---------------------------------------------------------------------------------------
# Icon set definitions. KEY = the telemetry string the webapp will look an icon up by.
# ---------------------------------------------------------------------------------------

# IntentType (enum) -> a representative source PNG under images/packed/intents/.
# Telemetry: monster_action.intents[] and turn_start.monsters[].intents[].type.
INTENT_MAP = {
    "Attack":       "images/packed/intents/attack/intent_attack_1.png",
    "Buff":         "images/packed/intents/intent_buff.png",
    "Debuff":       "images/packed/intents/intent_debuff.png",
    "DebuffStrong": "images/packed/intents/intent_debuff.png",  # no distinct static icon; shares Debuff
    "Defend":       "images/packed/intents/intent_defend.png",
    "Escape":       "images/packed/intents/intent_escape.png",
    "Heal":         "images/packed/intents/intent_heal.png",
    "Hidden":       "images/packed/intents/intent_hidden.png",
    "Summon":       "images/packed/intents/intent_summon.png",
    "Sleep":        "images/packed/intents/intent_sleep.png",
    "Stun":         "images/packed/intents/intent_stun.png",
    "StatusCard":   "images/packed/intents/intent_status_card.png",
    "CardDebuff":   "images/packed/intents/intent_card_debuff.png",
    "DeathBlow":    "images/packed/intents/intent_death_blow.png",
    "Unknown":      "images/packed/intents/intent_unknown.png",
}

# RoomType (enum) -> run-history screen icon. Telemetry: room_entered.room_type.
# Boss has no single generic icon (per-boss art only), so it is intentionally absent.
ROOM_MAP = {
    "Monster":  "images/ui/run_history/monster.png",
    "Elite":    "images/ui/run_history/elite.png",
    "Event":    "images/ui/run_history/event.png",
    "Shop":     "images/ui/run_history/shop.png",
    "RestSite": "images/ui/run_history/rest_site.png",
    "Treasure": "images/ui/run_history/treasure.png",
}

# Each set produces icons into a category. "dir" sets derive KEY from the filename; "explicit"
# sets use a fixed {KEY: res-path} map; "atlas" sets slice an atlas and key by sprite name.
SETS = [
    {
        "category": "potions", "kind": "dir", "dir": "images/potions", "recursive": False,
        "fields": ["potion_use.potion", "reward_taken.item (reward_type=potion)",
                   "shop_offered.items[].item (item_type=potion)", "shop_purchase.item (item_type=potion)"],
    },
    {
        "category": "relics", "kind": "dir", "dir": "images/relics", "recursive": False,
        "fields": ["relic_trigger.relic", "reward_taken.item (reward_type=relic)",
                   "rewards_offered.rewards[].item (reward_type=relic)",
                   "shop_offered.items[].item (item_type=relic)", "shop_purchase.item (item_type=relic)"],
    },
    {
        "category": "powers", "kind": "dir", "dir": "images/powers", "recursive": False,
        "fields": ["power_applied.power", "turn_start.players[].powers[].power",
                   "turn_start.monsters[].powers[].power"],
    },
    {
        "category": "cards", "kind": "dir", "dir": "images/packed/card_portraits", "recursive": True,
        "fields": ["card_play.card", "card_draw.card", "card_discard.card", "card_exhaust.card",
                   "rewards_offered.rewards[].item (reward_type=card)",
                   "reward_taken.item (reward_type=card)", "shop_offered.items[].item (item_type=card)",
                   "shop_purchase.item (item_type=card)"],
    },
    {
        "category": "orbs", "kind": "dir", "dir": "images/orbs", "recursive": False,
        # EMPTY_ORB/GLASS art exists but EMPTY isn't a channelable orb; only real orbs appear in telemetry.
        "skip_keys": ["EMPTY_ORB"],
        "fields": ["orb_channeled.orb"],
    },
    {
        "category": "intents", "kind": "explicit", "map": INTENT_MAP,
        "fields": ["monster_action.intents[]", "turn_start.monsters[].intents[].type"],
    },
    {
        "category": "rooms", "kind": "explicit", "map": ROOM_MAP,
        "fields": ["room_entered.room_type"],
    },
    {
        # Menu chrome: every sprite packed in ui_atlas, sliced via its .tpsheet. Named by
        # function (sprite name), no telemetry mapping.
        "category": "ui", "kind": "atlas", "atlas": "images/atlases/ui_atlas", "fields": [],
    },
    {
        # Loose menu UI PNGs not in an atlas (cursors, the run-history arrows, buttons).
        "category": "ui", "kind": "dir", "dir": "images/packed/common_ui", "recursive": False,
        "fields": [], "keep_filename": True,
    },
    {
        # Inline game-concept glyphs used in text: star_icon (Regent's stars -> stars_gained),
        # per-character *_energy_icon, plus gold/card/potion/chest. Named by function.
        "category": "resources", "kind": "dir", "dir": "images/packed/sprite_fonts", "recursive": False,
        "fields": [], "keep_filename": True,
    },
]

# ---------------------------------------------------------------------------------------
# GDRE Tools + pck resolution (mirrors scripts/extract-assets.sh)
# ---------------------------------------------------------------------------------------

def find_pck() -> str:
    if os.environ.get("STS2_PCK"):
        p = os.environ["STS2_PCK"]
        if os.path.isfile(p):
            return p
        sys.exit(f"STS2_PCK is set but not a file: {p}")
    sts2_dir = os.environ.get("STS2_DIR")
    if not sts2_dir:
        sys.exit("STS2_DIR is not set (export it to the folder containing sts2.dll).")
    names = ["Slay the Spire 2.pck", "SlayTheSpire2.pck", "game.pck", "data.pck"]
    dirs = [sts2_dir, os.path.join(sts2_dir, ".."), os.path.join(sts2_dir, "..", "..")]
    for d in dirs:
        if not os.path.isdir(d):
            continue
        d = os.path.abspath(d)
        for n in names:
            cand = os.path.join(d, n)
            if os.path.isfile(cand):
                return cand
        for f in sorted(os.listdir(d)):
            if f.endswith(".pck"):
                return os.path.join(d, f)
    sys.exit("Could not find the game .pck near STS2_DIR. Set STS2_PCK to its full path.")


def find_gdre() -> str:
    env = os.environ.get("GDRE_TOOLS_PATH")
    if env:
        if os.path.isfile(env) and os.access(env, os.X_OK):
            return env
        for sub in ("Contents/MacOS/Godot RE Tools", "Contents/MacOS/GDRE_tools"):
            cand = os.path.join(env, sub)
            if os.path.isfile(cand) and os.access(cand, os.X_OK):
                return cand
    from shutil import which
    for n in ("gdre_tools", "gdre_tools.x86_64", "Godot RE Tools"):
        cand = which(n)
        if cand:
            return cand
    if sys.platform == "darwin":
        for base in ("/Applications", os.path.expanduser("~/Applications")):
            for app in ("Godot RE Tools.app", "GDRE_tools.app"):
                for b in ("Godot RE Tools", "GDRE_tools"):
                    cand = os.path.join(base, app, "Contents/MacOS", b)
                    if os.path.isfile(cand) and os.access(cand, os.X_OK):
                        return cand
    sys.exit("GDRE Tools not found. Set GDRE_TOOLS_PATH (binary or .app), or install from "
             "https://github.com/GDRETools/gdsdecomp/releases")


def find_release_info(pck: str) -> dict:
    """Read the game's release_info.json (version/commit/date) — the same source the mod's
    ReleaseInfoManager uses for run_start.game_version. It sits next to the pck (macOS
    Contents/Resources), with a few STS2_DIR-relative fallbacks for Win/Linux layouts."""
    candidates = [os.path.join(os.path.dirname(pck), "release_info.json")]
    sts2 = os.environ.get("STS2_DIR")
    if sts2:
        candidates += [os.path.join(sts2, "release_info.json"),
                       os.path.join(sts2, "..", "release_info.json"),
                       os.path.join(sts2, "..", "..", "release_info.json")]
    for c in candidates:
        if os.path.isfile(c):
            try:
                return json.loads(Path(c).read_text())
            except Exception:
                pass
    return {}


def gdre_list_files(gdre: str, pck: str) -> list[str]:
    out = subprocess.run([gdre, "--headless", f"--list-files={pck}"],
                         capture_output=True, text=True, timeout=300)
    return [ln.strip() for ln in out.stdout.splitlines() if ln.strip().startswith("res://")]


def gdre_recover(gdre: str, pck: str, out_dir: str, includes: list[str]) -> None:
    args = [gdre, "--headless", f"--recover={pck}", f"--output={out_dir}"]
    args += [f"--include={inc}" for inc in includes]
    subprocess.run(args, capture_output=True, text=True, timeout=1200, check=True)

# ---------------------------------------------------------------------------------------
# Inventory helpers
# ---------------------------------------------------------------------------------------

# Imported texture data. Most textures compile to .ctex (VRAM-compressed); lossless ones
# (e.g. hardware cursors) compile to .image. Match both so nothing is silently skipped.
CTEX_RE = re.compile(r"^res://\.godot/imported/(?P<base>.+)\.png-[0-9a-f]{32}.*\.(ctex|image)$")


def build_ctex_index(all_files: list[str]) -> dict[str, list[str]]:
    """Map a source png basename (e.g. 'block_potion') -> its imported .ctex/.image path(s)."""
    idx: dict[str, list[str]] = {}
    for f in all_files:
        m = CTEX_RE.match(f)
        if m:
            idx.setdefault(m.group("base"), []).append(f)
    return idx


def res_to_local(recover_dir: Path, res_path: str) -> Path:
    return recover_dir / res_path[len("res://"):]

# ---------------------------------------------------------------------------------------
# Image normalization
# ---------------------------------------------------------------------------------------

def normalize_to_square(src: Path, dst: Path, size: int = ICON_SIZE) -> bool:
    """Fit the source image inside a size x size transparent canvas, centered, preserving aspect."""
    try:
        im = Image.open(src).convert("RGBA")
    except Exception as e:
        print(f"  ! could not open {src.name}: {e}", file=sys.stderr)
        return False
    im.thumbnail((size, size), Image.LANCZOS)
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.alpha_composite(im, ((size - im.width) // 2, (size - im.height) // 2))
    dst.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(dst)
    return True

# ---------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------

def main() -> None:
    clean = "--clean" in sys.argv
    pck = find_pck()
    gdre = find_gdre()
    release = find_release_info(pck)
    game_version = release.get("version", "unknown")
    print(f"GDRE : {gdre}")
    print(f"PCK  : {pck}")
    print(f"game : {game_version}")

    all_files = gdre_list_files(gdre, pck)
    ctex_index = build_ctex_index(all_files)
    file_set = set(all_files)
    print(f"pck inventory: {len(all_files)} files")

    # ---- Resolve every set into (category, key, res_png_path, keep_name) rows, and collect
    #      the res:// logical paths we must recover. --------------------------------------
    rows: list[dict] = []          # {category, key, res_path, keep_filename}
    atlas_sets: list[dict] = []    # atlas sets processed after recover (need the sheet on disk)
    logical_pngs: set[str] = set()
    tpsheets: set[str] = set()

    def add_png(res_path: str) -> None:
        logical_pngs.add(res_path)

    for s in SETS:
        cat = s["category"]
        if s["kind"] == "dir":
            prefix = "res://" + s["dir"] + "/"
            for f in all_files:
                if not f.startswith(prefix) or not f.endswith(".png.import"):
                    continue
                res_png = f[:-len(".import")]
                rel = res_png[len(prefix):]
                if not s.get("recursive", False) and "/" in rel:
                    continue
                if "/beta/" in res_png or res_png.endswith("/beta"):
                    continue  # skip alternate 'beta' art; canonical id only
                stem = Path(res_png).stem
                key = stem if s.get("keep_filename") else stem.upper()
                if key in s.get("skip_keys", []):
                    continue
                rows.append({"category": cat, "key": key, "res_path": res_png,
                             "keep_filename": s.get("keep_filename", False)})
                add_png(res_png)
        elif s["kind"] == "explicit":
            for key, rel in s["map"].items():
                res_png = "res://" + rel
                if res_png + ".import" not in file_set and res_png not in file_set:
                    print(f"  ! {cat}:{key} missing in pck ({rel}) — skipping", file=sys.stderr)
                    continue
                rows.append({"category": cat, "key": key, "res_path": res_png, "keep_filename": True})
                add_png(res_png)
        elif s["kind"] == "atlas":
            base = s["atlas"]  # e.g. images/atlases/ui_atlas
            tpsheet = "res://" + base + ".tpsheet"
            if tpsheet not in file_set:
                print(f"  ! atlas {base} has no .tpsheet — skipping", file=sys.stderr)
                continue
            tpsheets.add(tpsheet)
            atlas_sets.append(s)
            # atlas page textures are named <base>_N.png or <base>.png; include all + their ctex
            for f in all_files:
                if re.match(rf"^res://{re.escape(base)}(_\d+)?\.png\.import$", f):
                    add_png(f[:-len(".import")])

    # ---- Build the recover include list: every logical png + its .ctex, plus tpsheets. ---
    includes: list[str] = []
    includes += sorted(logical_pngs)
    includes += sorted(tpsheets)
    missing_ctex = 0
    for res_png in logical_pngs:
        base = Path(res_png).stem
        ctexes = ctex_index.get(base)
        if ctexes:
            includes += ctexes
        else:
            missing_ctex += 1
    includes = sorted(set(includes))
    print(f"sets resolved: {len(rows)} keyed icons + {len(atlas_sets)} atlas(es); "
          f"recover includes: {len(includes)} (missing ctex: {missing_ctex})")

    # ---- One recover pass. --------------------------------------------------------------
    recover_dir = OUTPUT_DIR / "_recover"
    if recover_dir.exists():
        import shutil
        shutil.rmtree(recover_dir)
    recover_dir.mkdir(parents=True, exist_ok=True)
    print("recovering assets (one GDRE pass; this can take a minute)...")
    gdre_recover(gdre, pck, str(recover_dir), includes)

    # ---- Emit output. -------------------------------------------------------------------
    if clean:
        import shutil
        for child in OUTPUT_DIR.iterdir():
            if child.name != "_recover":
                shutil.rmtree(child) if child.is_dir() else child.unlink()

    # category -> {key: relative_output_path}
    atlas_manifest: dict[str, list[str]] = {}
    catalog: dict[str, dict[str, str]] = {}
    counts: dict[str, int] = {}

    # dir + explicit rows
    for r in rows:
        src = res_to_local(recover_dir, r["res_path"])
        if not src.exists():
            print(f"  ! recovered file missing: {r['res_path']}", file=sys.stderr)
            continue
        out_rel = f"{r['category']}/{r['key']}.png"
        if normalize_to_square(src, OUTPUT_DIR / out_rel):
            catalog.setdefault(r["category"], {})[r["key"]] = out_rel
            counts[r["category"]] = counts.get(r["category"], 0) + 1

    # atlas rows (slice from the recovered sheet using the .tpsheet)
    for s in atlas_sets:
        cat = s["category"]
        base = s["atlas"]
        sheet_path = res_to_local(recover_dir, "res://" + base + ".tpsheet")
        if not sheet_path.exists():
            print(f"  ! recovered tpsheet missing for {base}", file=sys.stderr)
            continue
        sheet = json.loads(sheet_path.read_text())
        atlas_dir = sheet_path.parent
        for tex in sheet.get("textures", []):
            page_img = atlas_dir / tex["image"]
            if not page_img.exists():
                print(f"  ! atlas page missing: {tex['image']}", file=sys.stderr)
                continue
            page = Image.open(page_img).convert("RGBA")
            for sprite in tex.get("sprites", []):
                name = Path(sprite["filename"]).with_suffix("").as_posix().replace("/", "_")
                r = sprite["region"]
                crop = page.crop((r["x"], r["y"], r["x"] + r["w"], r["y"] + r["h"]))
                tmp = atlas_dir / f"_slice_{name}.png"
                crop.save(tmp)
                out_rel = f"{cat}/{name}.png"
                if normalize_to_square(tmp, OUTPUT_DIR / out_rel):
                    catalog.setdefault(cat, {})[name] = out_rel
                    counts[cat] = counts.get(cat, 0) + 1
                tmp.unlink(missing_ok=True)
                atlas_manifest.setdefault(cat, []).append(name)

    # ---- Write icons.json. --------------------------------------------------------------
    # Per-category telemetry fields (first set that defines the category wins for the field list).
    fields_by_cat: dict[str, list[str]] = {}
    for s in SETS:
        if s.get("fields") and s["category"] not in fields_by_cat:
            fields_by_cat[s["category"]] = s["fields"]

    atlas = {
        "game_version": game_version,
        "game_commit": release.get("commit"),
        "icon_size": ICON_SIZE,
        "key_rule": "For gameplay categories, KEY is the uppercased asset filename and equals the "
                    "telemetry id string. intents/rooms use the IntentType/RoomType enum name. "
                    "ui uses functional sprite names (no telemetry mapping).",
        "categories": {},
    }
    for cat in sorted(catalog):
        atlas["categories"][cat] = {
            "telemetry_fields": fields_by_cat.get(cat, []),
            "icons": dict(sorted(catalog[cat].items())),
        }

    (OUTPUT_DIR / "icons.json").write_text(json.dumps(atlas, indent=2) + "\n")

    # ---- Clean up the recover scratch dir. ----------------------------------------------
    import shutil
    shutil.rmtree(recover_dir, ignore_errors=True)

    total = sum(counts.values())
    print(f"\nDone. {total} icons -> {OUTPUT_DIR}")
    for cat in sorted(counts):
        print(f"  {cat:10} {counts[cat]}")
    print(f"  atlas       icons.json")


if __name__ == "__main__":
    main()
