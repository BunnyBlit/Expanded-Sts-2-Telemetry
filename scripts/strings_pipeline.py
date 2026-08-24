#!/usr/bin/env python3
"""Reproducible display-name pipeline for the telemetry webapp (all localized languages).

The telemetry stream emits ALL_CAPS ids (STRIKE_IRONCLAD, BURNING_BLOOD, VULNERABLE_POWER,
SILENT, ...). This builds a display-name rendering of those ids straight from the game's OWN
localization (res://localization/<code>/*.json, where every id carries a `.title`/`.name`) —
for EVERY language the game ships. So names track the game across versions and locales instead
of being hand-maintained or naively title-cased.

Output: strings_dist/<code>.json per locale (eng, deu, fra, jpn, ...) plus index.json listing
them. Each file is a per-category map { ID -> "Display Name" }, keyed the same way as icons.json
so the webapp can look a name up by the exact telemetry string. Pass locale codes as args to
limit the run (e.g. `build-strings.sh eng deu`). Sibling of the icon pipeline; run via
scripts/build-strings.sh. Pure stdlib (no Pillow venv needed).
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parent.parent
OUTPUT_DIR = PROJECT_ROOT / "strings_dist"

# Best-effort BCP-47 tag per game locale code (the code stays the canonical filename, since
# esp/spa are both Spanish and can't be safely collapsed). Unknown codes fall back to the code.
BCP47 = {
    "eng": "en-US", "deu": "de-DE", "esp": "es-ES", "fra": "fr-FR", "ita": "it-IT",
    "jpn": "ja-JP", "kor": "ko-KR", "pol": "pl-PL", "ptb": "pt-BR", "rus": "ru-RU",
    "spa": "es-419", "tha": "th-TH", "tur": "tr-TR", "zhs": "zh-Hans",
}
# The character "The Silent" -> "Silent" trim is English grammar; only apply it for eng.
STRIP_ARTICLE_LOCALE = "eng"

# RoomType (enum) display names — already plain English, so defined here rather than localized.
ROOM_NAMES = {
    "Monster": "Monster", "Elite": "Elite", "Boss": "Boss", "Event": "Event",
    "Shop": "Shop", "RestSite": "Rest Site", "Treasure": "Treasure",
}

# IntentType (PascalCase, as emitted) -> its localization key (UPPER_SNAKE) in intents.json.
# The game's title is a flavor word (Attack -> "Aggressive", Buff -> "Empower"); we surface it
# as the us-en render. Any IntentType absent from intents.json falls back to a prettified name.
INTENT_TYPES = ["Attack", "Buff", "Debuff", "DebuffStrong", "Defend", "Escape", "Heal",
                "Hidden", "Summon", "Sleep", "Stun", "StatusCard", "CardDebuff", "DeathBlow", "Unknown"]

SETS = [
    {"category": "characters", "file": "characters.json", "rule": "flat_title", "strip_the": True,
     "fields": ["run_start.character", "run_end.character", "turn_start.players[].character",
                "card_draw.character", "card_play.character"]},
    {"category": "cards", "file": "cards.json", "rule": "flat_title",
     "fields": ["card_play.card", "card_draw.card", "card_discard.card", "card_exhaust.card"]},
    {"category": "relics", "file": "relics.json", "rule": "flat_title",
     "fields": ["relic_trigger.relic", "reward_taken.item (reward_type=relic)"]},
    {"category": "potions", "file": "potions.json", "rule": "flat_title",
     "fields": ["potion_use.potion", "reward_taken.item (reward_type=potion)"]},
    {"category": "powers", "file": "powers.json", "rule": "flat_title",
     "fields": ["power_applied.power", "turn_start.players[].powers[].power",
                "turn_start.monsters[].powers[].power"]},
    {"category": "orbs", "file": "orbs.json", "rule": "flat_title",
     "fields": ["orb_channeled.orb"]},
    {"category": "events", "file": "events.json", "rule": "flat_title",
     "fields": ["event_choice.event"]},
    {"category": "monsters", "file": "monsters.json", "rule": "flat_name",
     "fields": ["monster_action.monster (model id)", "turn_start.monsters[].id (model id)"]},
    {"category": "rest_site", "file": "rest_site_ui.json", "rule": "prefixed_name", "prefix": "OPTION_",
     "fields": ["rest_site_choice.option"]},
    {"category": "intents", "file": "intents.json", "rule": "intents",
     "fields": ["monster_action.intents[]", "turn_start.monsters[].intents[].type"]},
    {"category": "rooms", "rule": "literal", "map": ROOM_NAMES,
     "fields": ["room_entered.room_type"]},
]

TAG_RE = re.compile(r"\[/?[^\]]*\]")  # game rich-text tags, e.g. [gold]...[/gold]


def clean(value: str) -> str:
    return TAG_RE.sub("", value).strip()


def pascal_to_upper_snake(name: str) -> str:
    return re.sub(r"(?<!^)(?=[A-Z])", "_", name).upper()


# ---------------------------------------------------------------------------------------
# GDRE + pck resolution (mirrors scripts/extract-assets.sh / icons_pipeline.py)
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
    for d in (sts2_dir, os.path.join(sts2_dir, ".."), os.path.join(sts2_dir, "..", "..")):
        if not os.path.isdir(d):
            continue
        d = os.path.abspath(d)
        for n in names:
            if os.path.isfile(os.path.join(d, n)):
                return os.path.join(d, n)
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
        if which(n):
            return which(n)
    if sys.platform == "darwin":
        for base in ("/Applications", os.path.expanduser("~/Applications")):
            for app in ("Godot RE Tools.app", "GDRE_tools.app"):
                for b in ("Godot RE Tools", "GDRE_tools"):
                    cand = os.path.join(base, app, "Contents/MacOS", b)
                    if os.path.isfile(cand) and os.access(cand, os.X_OK):
                        return cand
    sys.exit("GDRE Tools not found. Set GDRE_TOOLS_PATH, or install from "
             "https://github.com/GDRETools/gdsdecomp/releases")


def discover_locales(gdre: str, pck: str) -> list[str]:
    """List the localization/<code>/ folders the game ships (so new locales are picked up)."""
    out = subprocess.run([gdre, "--headless", f"--list-files={pck}"],
                         capture_output=True, text=True, timeout=300)
    codes = set()
    for ln in out.stdout.splitlines():
        m = re.match(r"^res://localization/([a-z]+)/", ln.strip())
        if m:
            codes.add(m.group(1))
    return sorted(codes)


# ---------------------------------------------------------------------------------------
# Extraction rules
# ---------------------------------------------------------------------------------------

def extract_flat_title(loc: dict, strip_the: bool = False) -> dict[str, str]:
    """Harvest every top-level `<ID>.title` (ID has no further dots => not a nested key)."""
    out: dict[str, str] = {}
    for key, val in loc.items():
        if not key.endswith(".title"):
            continue
        ident = key[: -len(".title")]
        if "." in ident:  # skip nested keys like MONSTER.moves.MOVE.title
            continue
        name = clean(val)
        if strip_the and name.startswith("The "):
            name = name[len("The "):]
        out[ident] = name
    return out


def extract_flat_name(loc: dict) -> dict[str, str]:
    """Harvest every top-level `<ID>.name` (ID has no further dots). Used where the game keys
    display names as .name rather than .title (e.g. monsters.json: AEONGLASS.name)."""
    out: dict[str, str] = {}
    for key, val in loc.items():
        if not key.endswith(".name"):
            continue
        ident = key[: -len(".name")]
        if "." in ident:
            continue
        out[ident] = clean(val)
    return out


def extract_prefixed_name(loc: dict, prefix: str) -> dict[str, str]:
    """rest_site_ui uses `OPTION_<X>.name`; key the result by <X> (matches rest_site_choice.option)."""
    out: dict[str, str] = {}
    for key, val in loc.items():
        if key.startswith(prefix) and key.endswith(".name"):
            ident = key[len(prefix): -len(".name")]
            out[ident] = clean(val)
    return out


def extract_intents(loc: dict) -> dict[str, str]:
    """Key by IntentType (as emitted); value = the game's flavor title, else a prettified name."""
    out: dict[str, str] = {}
    for intent in INTENT_TYPES:
        key = pascal_to_upper_snake(intent) + ".title"
        if key in loc:
            out[intent] = clean(loc[key])
        else:
            out[intent] = re.sub(r"(?<!^)(?=[A-Z])", " ", intent)  # DebuffStrong -> "Debuff Strong"
    return out


# ---------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------

def extract_category(s: dict, loc: dict, locale: str) -> dict[str, str]:
    if s["rule"] == "flat_title":
        strip = s.get("strip_the", False) and locale == STRIP_ARTICLE_LOCALE
        return extract_flat_title(loc, strip)
    if s["rule"] == "flat_name":
        return extract_flat_name(loc)
    if s["rule"] == "prefixed_name":
        return extract_prefixed_name(loc, s["prefix"])
    if s["rule"] == "intents":
        return extract_intents(loc)
    return {}


def build_locale(scratch: Path, locale: str) -> tuple[dict, int]:
    """Extract every category for one locale from its already-recovered files."""
    def load(fname: str) -> dict:
        p = scratch / "localization" / locale / fname
        return json.loads(p.read_text()) if p.exists() else {}

    categories: dict[str, dict] = {}
    total = 0
    for s in SETS:
        cat = s["category"]
        if s["rule"] == "literal":
            names = dict(s["map"])  # RoomType names are English-only labels; shared across locales
        else:
            data = load(s["file"])
            if not data:
                print(f"  ! [{locale}] {s['file']} not recovered — skipping {cat}", file=sys.stderr)
                continue
            names = extract_category(s, data, locale)
        categories[cat] = {
            "telemetry_fields": s.get("fields", []),
            "names": dict(sorted(names.items())),
        }
        total += len(names)

    doc = {
        "locale": locale,
        "bcp47": BCP47.get(locale, locale),
        "note": "KEY is the exact telemetry id string; value is the display name from the game's "
                "own localization for this locale. English character names have their leading "
                "'The ' stripped. Intent names are the game's flavor titles (e.g. Attack -> "
                "'Aggressive'). rooms are English-only enum labels.",
        "categories": categories,
    }
    return doc, total


def main() -> None:
    pck = find_pck()
    gdre = find_gdre()
    print(f"GDRE : {gdre}")
    print(f"PCK  : {pck}")

    only = [a for a in sys.argv[1:] if not a.startswith("-")]  # optional locale filter, e.g. `eng deu`
    locales = discover_locales(gdre, pck)
    if only:
        locales = [l for l in locales if l in only] or only
    print(f"locales: {', '.join(locales)}")

    files = sorted({s["file"] for s in SETS if "file" in s})
    scratch = OUTPUT_DIR / "_loc"
    import shutil
    if scratch.exists():
        shutil.rmtree(scratch)
    scratch.mkdir(parents=True, exist_ok=True)

    includes = [f"--include=res://localization/{loc}/{f}" for loc in locales for f in files]
    print(f"recovering {len(files)} files x {len(locales)} locales ({len(includes)} includes)...")
    subprocess.run([gdre, "--headless", f"--recover={pck}", f"--output={scratch}", *includes],
                   capture_output=True, text=True, timeout=600, check=True)

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    index = []
    for locale in locales:
        doc, total = build_locale(scratch, locale)
        (OUTPUT_DIR / f"{locale}.json").write_text(
            json.dumps(doc, indent=2, ensure_ascii=False) + "\n")
        index.append({"locale": locale, "bcp47": doc["bcp47"], "file": f"{locale}.json", "count": total})
        print(f"  {locale:5} {doc['bcp47']:7} {total} names")

    (OUTPUT_DIR / "index.json").write_text(
        json.dumps({"locales": index}, indent=2, ensure_ascii=False) + "\n")

    shutil.rmtree(scratch, ignore_errors=True)
    print(f"\nDone. {len(locales)} locales -> {OUTPUT_DIR} (see index.json)")


if __name__ == "__main__":
    main()
