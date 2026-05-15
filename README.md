# expanded-telemetry

A Slay the Spire 2 mod that streams full-run telemetry to disk in real-time as NDJSON. Purely observational — does not affect gameplay.

This mod was written with AI (Claude Code) along with the [STS2 Modding MCP Server](https://github.com/elliotttate/sts2-modding-mcp).

## What it does

The game already records run history at the floor level (wins, losses, relics, etc.). This mod adds a streaming, real-time transcript of the entire run: every floor traversed, every event choice, every shop purchase, every rest site decision, every reward screen — plus fine-grained combat data (card draws/plays/discards, damage, block, powers, orbs, monster actions, relic triggers) all tagged with floor, encounter, player, and turn number.

Output sits alongside the game's own `.run` files:

```
~/Library/Application Support/SlayTheSpire2/steam/{UserID}/modded/profile{N}/saves/history/
  1776012547.run               ← game's file
  1776012547.expanded_run   ← this mod's file
```

While a run is in progress the file is named `in_progress.expanded_run` and written with `AutoFlush=true` (crash-safe, readable mid-run). It is renamed to `{StartTime}.expanded_run` when the run ends.

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
| `room_entered` | `room_type`, `floor`, `act`, `player`, `timestamp` |
| `combat_start` | `encounter`, `timestamp` |
| `turn_start` | `encounter`, `turn`, `players` (array), `monsters` (array), `timestamp` |
| `card_draw` | `encounter`, `card`, `player`, `from_hand_draw`, `turn`, `upgrade_level`, `timestamp` |
| `card_play` | `encounter`, `card`, `player`, `target` (null if untargeted), `turn`, `upgrade_level`, `is_auto_play`, `timestamp` |
| `card_discard` | `encounter`, `card`, `player`, `from_flush`, `turn`, `upgrade_level`, `timestamp` |
| `card_exhaust` | `encounter`, `card`, `player`, `from_ethereal`, `turn`, `upgrade_level`, `timestamp` |
| `potion_use` | `encounter`, `potion`, `player`, `target` (null if untargeted), `turn`, `timestamp` |
| `power_applied` | `encounter`, `power`, `target`, `applier` (null if self-decremented), `amount`, `turn`, `timestamp` |
| `damage_dealt` | `encounter`, `target`, `dealer` (null if no source creature), `hp_lost`, `blocked`, `overkill`, `turn`, `timestamp` |
| `block_gained` | `encounter`, `target`, `amount`, `turn`, `timestamp` |
| `orb_channeled` | `encounter`, `player`, `orb`, `turn`, `timestamp` — Defect only |
| `stars_gained` | `encounter`, `player`, `amount`, `turn`, `timestamp` — stars characters only |
| `relic_trigger` | `encounter`, `relic`, `player`, `targets` (array of creature IDs), `turn`, `timestamp` |
| `monster_action` | `encounter`, `monster`, `move`, `intents` (array of strings), `targets` (array of creature IDs), `turn`, `timestamp` |
| `turn_end` | `encounter`, `turn`, `timestamp` |
| `combat_end` | `encounter`, `outcome` (`"victory"` or `"defeat"`), `timestamp` |
| `rewards_offered` | `rewards` (array of `{reward_type, item}`), `floor`, `player`, `timestamp` |
| `reward_taken` | `reward_type`, `item` (null for card/gold), `amount` (gold only), `floor`, `player`, `timestamp` |
| `event_choice` | `event`, `option_key`, `floor`, `player`, `timestamp` |
| `rest_site_choice` | `option`, `floor`, `player`, `timestamp` |
| `shop_offered` | `items` (array of `{item_type, item, cost}`), `floor`, `player`, `timestamp` |
| `shop_purchase` | `item_type`, `item` (null for card_removal), `gold_spent`, `floor`, `player`, `timestamp` |
| `run_end` | `win`, `abandoned`, `character`, `ascension`, `num_players`, `timestamp` |

### Disambiguating fields

- **`from_hand_draw`** (bool on `card_draw`): `true` = drawn as part of the start-of-turn hand deal; `false` = drawn by a card effect, power, or relic mid-turn.
- **`from_flush`** (bool on `card_discard`): `true` = discarded as part of the end-of-turn hand flush; `false` = discarded by an explicit effect (card like Acrobatics or Calculated Gamble, a boss mechanic, etc.).
- **`from_ethereal`** (bool on `card_exhaust`): `true` = card had the Ethereal keyword and was auto-exhausted at end of turn (e.g. Dazed); `false` = exhausted by an explicit card or power effect (e.g. Slimed status cards).
- **`target`** (string or null on `card_play`, `potion_use`): creature model ID of the target; `null` for untargeted/AOE effects. On `potion_use` specifically: the player's own creature ID for self-targeted potions (e.g. Block Potion), a monster ID for targeted potions (e.g. Fire Potion), and `null` for AOE potions (e.g. Explosive Ampoule).
- **`players`** (array on `turn_start`): snapshot of each player's state at turn start — `player` (NetId), `character`, `hp`, `max_hp`, `block`, `energy`, `max_energy`, `powers` (array of `{power, amount}`). Block is 0 for the player at turn start since it resets each turn.
- **`monsters`** (array on `turn_start`): snapshot of each living enemy at turn start — `id`, `hp`, `max_hp`, `block`, `powers` (array of `{power, amount}`), `intents` (array of `{type}` where type is the `IntentType` string e.g. `"Attack"`, `"Buff"`, `"Debuff"`). An empty `intents` list means no move has been rolled yet (e.g. a monster that just spawned).
- **`turn`** (int on most combat events): the turn number within the current combat. `turn_start` and `turn_end` bracket each player turn; all events between them carry the matching `turn` value. **`turn:0`** is a special "combat setup" phase covering events that fire before the first player turn begins (e.g. the Defect's starting orb channel, relic block grants from `BeforeCombatStart`).
- **`upgrade_level`** (int on card events): `0` = base card, `1` = upgraded, `2+` = double-upgraded. Present on `card_draw`, `card_play`, `card_discard`, and `card_exhaust`.
- **`is_auto_play`** (bool on `card_play`): `true` = card was played automatically by a power or relic effect; `false` = played by the player.
- **`room_type`** (string on `room_entered`): one of `Monster`, `Elite`, `Boss`, `Event`, `Shop`, `RestSite`, `Treasure`. Every floor produces a `room_entered` before any floor-specific events.
- **`floor`** (int on out-of-combat events): the current floor number (`IRunState.TotalFloor`). Monotonically increasing across the run. Use to correlate `rewards_offered` + `reward_taken` pairs, `shop_offered` + `shop_purchase` pairs, and to join out-of-combat events to the floor traversal log.
- **`act`** (int on `room_entered`): 1, 2, or 3.
- **`option_key`** (string on `event_choice`): the `TextKey` of the chosen event option — same key the game writes to its own run history (e.g. `"big_fish.options.GAIN_MAX_HP"`).
- **`option`** (string on `rest_site_choice`): the rest site `OptionId` — one of `HEAL`, `SMITH`, `DIG`, `CLONE`, `MEND`, `LIFT`, `HATCH`, `COOK`.
- **`items`** (array on `shop_offered`): the full shop inventory at the time the shop opens. Each element is `{item_type, item, cost}` where `item_type` is `"card"` / `"relic"` / `"potion"` / `"card_removal"`, `item` is the model ID (null for `card_removal`), and `cost` is the gold price. Correlate with `shop_purchase` on `floor` + `player` to find what was available but not bought.
- **`item_type`** (string on `shop_purchase`): `"card"`, `"relic"`, `"potion"`, or `"card_removal"`. `item` is null for `card_removal`.
- **`rewards`** (array on `rewards_offered`): all reward options shown on the screen. Each element is `{reward_type, item}` where `reward_type` is `"card"` / `"relic"` / `"potion"` / `"gold"` and `item` is the model ID (or gold amount as a string). A single `CardReward` with 3 options produces 3 entries. Correlate with `reward_taken` on `floor` + `player` to find what was offered but skipped.
- **`reward_type`** (string on `reward_taken`): same values as in `rewards_offered`. `"card"` rewards omit `item` (which specific card was taken is not available at hook time); `"gold"` rewards include `amount` instead of `item`.
- **`relic`** (string on `relic_trigger`): the relic model ID that activated (e.g. `"ANCHOR"`, `"CENTENNIAL_PUZZLE"`).
- **`monster`** (string on `monster_action`): creature model ID of the monster that acted (e.g. `"GREMLIN_NOB"`).
- **`move`** (string on `monster_action`): the move state ID the monster performed (e.g. `"BASH"`, `"THRASH"`, `"STUNNED"`).
- **`intents`** (array on `monster_action`): `IntentType` strings describing the move (e.g. `["Attack"]`, `["Buff"]`, `["Debuff", "Attack"]`). Confirms what actually happened vs. the intent snapshot in `turn_start`.
- **`targets`** (array on `monster_action`): creature model IDs of the targets. Empty for untargeted moves (buffs, heals, escapes). Non-empty for attacks — typically the player creature(s).
- **`amount`** (int on `power_applied`, `block_gained`, `stars_gained`): stack/amount change. Can be **negative** on `power_applied` when a power self-decrements (e.g. Poison ticking down at end of turn).
- **`applier`** (string or null on `power_applied`): creature model ID of whatever applied the power; `null` when the power decrements on its own.
- **`hp_lost`**, **`blocked`**, **`overkill`** (ints on `damage_dealt`): breakdown of a single damage instance. `hp_lost` = HP actually removed; `blocked` = absorbed by block; `overkill` = excess damage beyond remaining HP (> 0 on killing blows only).

## Build & deploy

Requires .NET 9 SDK. Set the `STS2_DIR` environment variable to the folder containing `sts2.dll` — add this to your shell profile (`.zprofile`, `.zshrc`, etc.) so it persists:

```bash
# macOS
export STS2_DIR="/Users/YOU/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64"

# Windows
$env:STS2_DIR = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\SlayTheSpire2_Data\Managed"

# Linux
export STS2_DIR="$HOME/.steam/steam/steamapps/common/Slay the Spire 2/SlayTheSpire2_Data/Managed"
```

Then build:

```bash
./deploy.sh       # dotnet build -c Debug + auto-copies DLL to game mods folder
./fetch-log.sh    # copies the game log to logs/godot.log for inspection
```

## Tools built

- **`sts2-runtime-tester` agent** (`.claude/agents/sts2-runtime-tester.md`): Claude Code subagent that builds, deploys, runs AutoSlay, and reads telemetry output to verify invariants. Invoke via the Agent tool with `subagent_type: "sts2-runtime-tester"`.

## TODO

### Needs manual verification
- [ ] **`outcome: "defeat"` on `combat_end`**: Die in a combat and confirm the event is emitted with `outcome: "defeat"`. AutoSlay never loses, so this can't be verified automatically. The code path goes through `CombatManager.LoseCombat` → `LoseCombatPatch`.
- [ ] **`from_flush: false` with a discard-synergy deck**: Play a run as Silent (pick up cards like Acrobatics, Calculated Gamble, Survivor, or the Gambling Chip relic) and confirm that explicit mid-combat discards emit `from_flush: false`. AutoSlay hasn't reliably produced these.
- [ ] **`is_auto_play: false` on `card_play`**: AutoSlay drives all plays programmatically, so every play in an AutoSlay run is flagged as auto-play. Play a card manually in combat and confirm `is_auto_play: false` is emitted.

### Future features
- [x] **Player and monster state on `turn_start`**: Player HP/block/energy/powers and monster HP/block/powers/intents snapshotted at the start of each player turn. Combined with the existing delta events (`damage_dealt`, `block_gained`, `power_applied`) this gives full mid-turn reconstruction without the redundancy of a `turn_end` snapshot.
- [x] **Monster action events**: `monster_action` events record what each monster does on its turn — monster ID, move name, intent types, and targets. Fires between `turn_end N` and `turn_start N+1`.
- [x] **Relic trigger events**: `relic_trigger` events fire whenever a relic flashes during combat — relic ID, owner player, and the creatures targeted by the flash.
- [x] **Non-combat room telemetry**: `room_entered` (every floor), `event_choice`, `rest_site_choice`, `shop_purchase`, `shop_offered`, `rewards_offered`, `reward_taken` — full run transcript matching the game's own `.run` log coverage.
- [x] **Sensible output file extension**: files are now named `*.expanded_run` (previously `*.encounter_cards`).
- [x] **No hardcoded install paths**: build tooling reads `STS2_DIR` from the environment; in-mod paths use game APIs exclusively.
- [ ] **Configurable file suffix**: via mod config (deferred — BaseLib `SimpleModConfig` supports enum dropdowns, not free-text)
- [ ] **Stream to a telemetry ingest server**: to, ya know, do something with the data