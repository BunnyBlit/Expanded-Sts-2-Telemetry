# expanded-telemetry

A Slay the Spire 2 mod that streams per-encounter card telemetry to disk in real-time as NDJSON. Purely observational — does not affect gameplay.

This mod was written with AI (Claude Code) along with the [STS2 Modding MCP Server](https://github.com/elliotttate/sts2-modding-mcp).

## What it does

The game already records run history at the floor level (wins, losses, relics, etc.). This mod adds finer-grained data: every card draw, play, and discard within each combat encounter, tagged with encounter ID, player, and turn number.

Output sits alongside the game's own `.run` files:

```
~/Library/Application Support/SlayTheSpire2/steam/{UserID}/modded/profile{N}/saves/history/
  1776012547.run               ← game's file
  1776012547.encounter_cards   ← this mod's file
```

While a run is in progress the file is named `in_progress.encounter_cards` and written with `AutoFlush=true` (crash-safe, readable mid-run). It is renamed to `{StartTime}.encounter_cards` when the run ends.

## Output format

One JSON object per line (NDJSON). Example sequence from a single combat:

```json
{"event_type":"combat_start","encounter":"GREMLIN_NOB","timestamp":1776012550}
{"event_type":"turn_start","encounter":"GREMLIN_NOB","turn":1,"timestamp":1776012550}
{"event_type":"card_draw","encounter":"GREMLIN_NOB","card":"STRIKE_RED","player":1,"from_hand_draw":true,"turn":1,"timestamp":1776012550}
{"event_type":"card_draw","encounter":"GREMLIN_NOB","card":"DEFEND_RED","player":1,"from_hand_draw":true,"turn":1,"timestamp":1776012550}
{"event_type":"card_play","encounter":"GREMLIN_NOB","card":"STRIKE_RED","player":1,"target":"GREMLIN_NOB","turn":1,"timestamp":1776012551}
{"event_type":"card_discard","encounter":"GREMLIN_NOB","card":"DEFEND_RED","player":1,"from_flush":true,"turn":1,"timestamp":1776012552}
{"event_type":"turn_end","encounter":"GREMLIN_NOB","turn":1,"timestamp":1776012552}
{"event_type":"combat_end","encounter":"GREMLIN_NOB","outcome":"victory","timestamp":1776012553}
```

### Event reference

| Event | Fields |
|-------|--------|
| `run_start` | `timestamp` |
| `combat_start` | `encounter`, `timestamp` |
| `turn_start` | `encounter`, `turn`, `timestamp` |
| `card_draw` | `encounter`, `card`, `player`, `from_hand_draw`, `turn`, `upgrade_level`, `timestamp` |
| `card_play` | `encounter`, `card`, `player`, `target` (null if untargeted), `turn`, `upgrade_level`, `is_auto_play`, `timestamp` |
| `card_discard` | `encounter`, `card`, `player`, `from_flush`, `turn`, `upgrade_level`, `timestamp` |
| `card_exhaust` | `encounter`, `card`, `player`, `from_ethereal`, `turn`, `upgrade_level`, `timestamp` |
| `potion_use` | `encounter`, `potion`, `player`, `target` (null if untargeted), `turn`, `timestamp` |
| `turn_end` | `encounter`, `turn`, `timestamp` |
| `combat_end` | `encounter`, `outcome` (`"victory"` or `"defeat"`), `timestamp` |
| `run_end` | `win`, `abandoned`, `character`, `ascension`, `num_players`, `timestamp` |

### Disambiguating fields

- **`from_hand_draw`** (bool on `card_draw`): `true` = drawn as part of the start-of-turn hand deal; `false` = drawn by a card effect, power, or relic mid-turn.
- **`from_flush`** (bool on `card_discard`): `true` = discarded as part of the end-of-turn hand flush; `false` = discarded by an explicit effect (card like Acrobatics or Calculated Gamble, a boss mechanic, etc.).
- **`from_ethereal`** (bool on `card_exhaust`): `true` = card had the Ethereal keyword and was auto-exhausted at end of turn (e.g. Dazed); `false` = exhausted by an explicit card or power effect (e.g. Slimed status cards).
- **`target`** (string or null on `potion_use`): monster/creature ID for targeted potions; the player's own character ID for self-targeted potions (e.g. Block Potion); `null` for untargeted/AOE potions (e.g. Explosive Ampoule).
- **`turn`** (int on card/potion events): the turn number within the current combat, starting at 1. `turn_start` and `turn_end` bracket each turn; all card and potion events between them carry the matching `turn` value.
- **`upgrade_level`** (int on card events): `0` = base card, `1` = upgraded, `2+` = double-upgraded. Present on `card_draw`, `card_play`, `card_discard`, and `card_exhaust`.
- **`is_auto_play`** (bool on `card_play`): `true` = card was played automatically by a power or relic effect; `false` = played by the player.

## Build & deploy

```bash
./deploy.sh       # dotnet build -c Debug + auto-copies DLL to game mods folder
./fetch-log.sh    # copies the game log to logs/godot.log for inspection
```

Requires .NET 9 SDK. The mod targets `SlayTheSpire2.app/Contents/MacOS/mods/expanded-telemetry/`.

## Tools built

- **`sts2-runtime-tester` agent** (`.claude/agents/sts2-runtime-tester.md`): Claude Code subagent that builds, deploys, runs AutoSlay, and reads telemetry output to verify invariants. Invoke via the Agent tool with `subagent_type: "sts2-runtime-tester"`.

## TODO

### Needs manual verification
- [ ] **`outcome: "defeat"` on `combat_end`**: Die in a combat and confirm the event is emitted with `outcome: "defeat"`. AutoSlay never loses, so this can't be verified automatically. The code path goes through `CombatManager.LoseCombat` → `LoseCombatPatch`.
- [ ] **`from_flush: false` with a discard-synergy deck**: Play a run as Silent (pick up cards like Acrobatics, Calculated Gamble, Survivor, or the Gambling Chip relic) and confirm that explicit mid-combat discards emit `from_flush: false`. AutoSlay hasn't reliably produced these.
- [ ] **`is_auto_play: false` on `card_play`**: AutoSlay drives all plays programmatically, so every play in an AutoSlay run is flagged as auto-play. Play a card manually in combat and confirm `is_auto_play: false` is emitted.

### Future features
- [ ] **`power_applied`, `damage_dealt`, `block_gained`, `orb_channeled`, `stars_gained` events**: Track the core combat resources and state changes that currently have no telemetry coverage. Powers (Strength, Vulnerable, Focus, etc.) and damage/block outcomes are fundamental to understanding combat effectiveness.
- [ ] **Player and monster state on `turn_start` / `turn_end`**: Include current HP, block, energy, and active powers for the player and each monster at the start and end of each turn, providing a full snapshot of combat state each round.
- [ ] **Monster action events**: Record what each monster does on its turn (attack, buff, debuff, etc.) so the full combat transcript includes both sides.
- [ ] **Relic trigger events**: Emit an event when a relic activates during combat, identifying which relic fired and in what context.
- [ ] **Non-combat room telemetry**: shop visits, event choices, rest site interactions
- [ ] **Configurable file names**: (temp name and final extension) via mod config
- [ ] **Stream to a telemetry ingest server**: to, ya know, do something with the data
- [ ] **Remove hardcoded paths**: there are some hardcoded paths that we should derive from env vars (like the sts2 data and application directories). Probably should set up an init or install script to do that thing.