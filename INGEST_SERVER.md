# Ingest Server Specification

This document describes what a remote ingest server receives from the `expanded-telemetry` mod. It covers the HTTP transport, batching behavior, reliability model, and the complete event schema.

## Transport

- **Method**: `POST`
- **Content-Type**: `application/x-ndjson`
- **Endpoint**: configured by the user in `expanded-telemetry.cfg` → `ServerUrl`
- **No authentication headers** are sent — add a reverse proxy or shared-secret query param on your end if needed

### Batching

The mod does not send one HTTP request per event. Events are queued in memory and POSTed in batches:

- Drain interval: **every 200ms**
- Max batch size: **100 events per POST** (larger queues produce multiple back-to-back requests)
- Each POST body is multiple JSON lines joined by `\n` (no trailing newline guaranteed)
- At run end, one final drain is attempted with a **3-second timeout** before the stream closes

### Response handling

The mod ignores the response body and status code. Any network error (timeout, 4xx, 5xx, connection refused) causes the batch to be dropped silently. There is **no retry logic**.

A `200 OK` with an empty body is the recommended response. Processing asynchronously on the server side is fine.

## Reliability model

This is a **fire-and-forget** stream. Design for it:

| Property | Behavior |
|----------|----------|
| Delivery | At-most-once per event |
| Ordering | Preserved within a single run (events are enqueued in emission order) |
| Retries | None — failed batches are dropped |
| Backpressure | None — game thread never blocks; queue overflows drop the oldest enqueue attempts |
| Duplicates | Not expected under normal conditions; possible if the game crashes mid-flush and restarts |
| Out-of-order across runs | Not possible — each run has its own sequential stream |

**Queue overflow**: if the server is unreachable and the in-memory queue exceeds 2000 events, new events are dropped. The mod logs a warning in the game log.

## Run boundaries

Every run produces exactly:
- One `run_start` event (first event in the stream, before any room or combat events)
- One `run_end` event (last event in the stream, after all combat and floor events)

You can use `run_start.timestamp` as a logical run ID for correlation — it is the same value used to name the local `.expanded_run` file (`{timestamp}.expanded_run`).

**There is no explicit `run_id` field on events.** For single-player, the stream is sequential so events arrive in run order. For multiplayer, if you expect concurrent runs from the same client, you will need to track run boundaries via `run_start` / `run_end` timestamps.

## Event format

Each event is a single JSON object. The mod emits events as compact (no-whitespace) JSON. The `\n` separator between events within a batch is the NDJSON delimiter — parse each line independently.

All events have:
- `"event_type"`: string — the event name (see reference below)
- `"timestamp"`: integer — Unix seconds UTC at emission time

All other fields are event-specific.

### String ID conventions

- **`encounter`**: the monster group's model ID (e.g. `"GREMLIN_NOB"`, `"JAW_WORM"`)
- **`card`**: card model ID (e.g. `"STRIKE_RED"`, `"THUNDERCLAP"`)
- **`player`**: Steam64 ID (same as `NetId`) as a 64-bit unsigned integer (ulong JSON number) — stable per account across all sessions. Present on **every event** as the session owner.
- **`character`**: character model ID (e.g. `"IRONCLAD"`, `"DEFECT"`)
- **Creature instance IDs** (`target`, `dealer`, `applier`, `monster`, `monsters[].id`, `targets[]`): `"{ModelId}:{CombatId}"` format (e.g. `"GREMLIN_NOB:1"`, `"IRONCLAD_BODY:0"`). `CombatId` is a uint assigned sequentially when each creature joins combat — two enemies of the same type get different values. Scoped to the current encounter; the same numeric suffix may recur in a different combat.
- **`power`**: power model ID (e.g. `"POISON"`, `"STRENGTH"`)
- **`relic`**: relic model ID (e.g. `"ANCHOR"`, `"CENTENNIAL_PUZZLE"`)
- **`orb`**: orb model ID (e.g. `"LIGHTNING"`, `"DARK"`, `"FROST"`)
- **`potion`**: potion model ID (e.g. `"FIRE_POTION"`, `"BLOCK_POTION"`)

## Event reference

### Run lifecycle

#### `run_start`
First event of every run.
```json
{"event_type":"run_start","player":76561197983754930,"timestamp":1776012547}
```

#### `run_end`
Last event of every run.
```json
{"event_type":"run_end","win":true,"abandoned":false,"character":"IRONCLAD","ascension":0,"num_players":1,"player":76561197983754930,"timestamp":1776015000}
```
| Field | Type | Notes |
|-------|------|-------|
| `win` | bool | true if the run was won |
| `abandoned` | bool | true if the run was abandoned mid-run |
| `character` | string | character model ID |
| `ascension` | int | ascension level (0 = no ascension) |
| `num_players` | int | number of players in a co-op run |

---

### Floor traversal

#### `room_entered`
Fires for every floor, every room type. Always appears before floor-specific events (`combat_start`, `shop_offered`, etc.).
```json
{"event_type":"room_entered","room_type":"Monster","floor":1,"act":1,"player":76561197983754930,"timestamp":1776012548}
```
| Field | Type | Notes |
|-------|------|-------|
| `room_type` | string | one of: `Monster`, `Elite`, `Boss`, `Event`, `Shop`, `RestSite`, `Treasure` |
| `floor` | int | monotonically increasing floor number across the run |
| `act` | int | 1, 2, or 3 |
| `player` | ulong | Steam64 session owner (same as `NetId`, stable per account) |

---

### Combat events

#### `combat_start`
```json
{"event_type":"combat_start","encounter":"GREMLIN_NOB","player":76561197983754930,"timestamp":1776012549}
```

#### `turn_start`
Fires before card draws for that turn. Includes full state snapshots.
```json
{
  "event_type": "turn_start",
  "encounter": "GREMLIN_NOB",
  "turn": 1,
  "player": 76561197983754930,
  "players": [
    {
      "player": 76561197983754930,
      "character": "IRONCLAD",
      "hp": 80,
      "max_hp": 80,
      "block": 0,
      "energy": 3,
      "max_energy": 3,
      "powers": [{"power": "STRENGTH", "amount": 2}]
    }
  ],
  "monsters": [
    {
      "id": "GREMLIN_NOB:1",
      "hp": 85,
      "max_hp": 85,
      "block": 0,
      "powers": [],
      "intents": [{"type": "Attack"}]
    }
  ],
  "timestamp": 1776012550
}
```
- `turn: 0` is a special pre-combat phase covering events that fire before the first player turn (e.g. Defect starting orbs, relic block grants)
- `monsters` contains only living enemies at the time of the snapshot
- `intents` may be empty for monsters that just spawned this turn
- `intents[].type` values: `"Attack"`, `"Buff"`, `"Debuff"`, `"Defend"`, `"Hidden"`, and others from the `IntentType` enum

#### `turn_end`
```json
{"event_type":"turn_end","encounter":"GREMLIN_NOB","turn":1,"player":76561197983754930,"timestamp":1776012555}
```

#### `combat_end`
```json
{"event_type":"combat_end","encounter":"GREMLIN_NOB","outcome":"victory","player":76561197983754930,"timestamp":1776012560}
```
`outcome` is `"victory"` or `"defeat"`.

---

### Card events

All card events include `encounter`, `card`, `player` (NetId ulong), `character`, `turn`, `upgrade_level`, and `timestamp`.

`upgrade_level`: `0` = base, `1` = upgraded, `2+` = double-upgraded.

#### `card_draw`
```json
{"event_type":"card_draw","encounter":"GREMLIN_NOB","card":"STRIKE_RED","player":76561197983754930,"character":"IRONCLAD","from_hand_draw":true,"turn":1,"upgrade_level":0,"timestamp":1776012550}
```
`from_hand_draw`: `true` = drawn as part of start-of-turn hand deal; `false` = drawn by a card/power/relic effect mid-turn.

#### `card_play`
```json
{"event_type":"card_play","encounter":"GREMLIN_NOB","card":"STRIKE_RED","player":76561197983754930,"character":"IRONCLAD","target":"GREMLIN_NOB:1","turn":1,"upgrade_level":0,"is_auto_play":false,"timestamp":1776012551}
```
- `target`: creature instance ID (`{ModelId}:{CombatId}`) of the target, or `null` for untargeted/AOE cards
- `is_auto_play`: `true` if triggered by a power or relic; `false` if played by the player directly

#### `card_discard`
```json
{"event_type":"card_discard","encounter":"GREMLIN_NOB","card":"DEFEND_RED","player":76561197983754930,"character":"IRONCLAD","from_flush":true,"turn":1,"upgrade_level":0,"timestamp":1776012554}
```
`from_flush`: `true` = end-of-turn hand flush; `false` = explicit discard by a card/power effect (e.g. Acrobatics, boss mechanic).

#### `card_exhaust`
```json
{"event_type":"card_exhaust","encounter":"GREMLIN_NOB","card":"DAZED","player":76561197983754930,"character":"IRONCLAD","from_ethereal":true,"turn":1,"upgrade_level":0,"timestamp":1776012554}
```
`from_ethereal`: `true` = auto-exhausted at end of turn due to the Ethereal keyword; `false` = exhausted by an explicit card or power effect.

---

### Combat resource events

#### `damage_dealt`
Fires for every damage instance (player takes damage and monster takes damage).
```json
{"event_type":"damage_dealt","encounter":"GREMLIN_NOB","target":"IRONCLAD_BODY:0","dealer":"GREMLIN_NOB:1","hp_lost":14,"blocked":0,"overkill":0,"turn":1,"player":76561197983754930,"timestamp":1776012556}
```
- `dealer`: null if the damage has no source creature
- `overkill`: excess damage past 0 HP (> 0 only on killing blows)

#### `block_gained`
```json
{"event_type":"block_gained","encounter":"GREMLIN_NOB","target":"IRONCLAD_BODY:0","amount":5,"turn":1,"player":76561197983754930,"timestamp":1776012552}
```
Fires for all block gains including large relic-granted values at combat start (e.g. Plating).

#### `power_applied`
```json
{"event_type":"power_applied","encounter":"GREMLIN_NOB","power":"POISON","target":"GREMLIN_NOB:1","applier":"IRONCLAD_BODY:0","amount":3,"turn":1,"player":76561197983754930,"timestamp":1776012553}
```
- `applier`: null when the power self-decrements (e.g. Poison ticking down at end of turn)
- `amount`: negative when a power self-decrements

#### `potion_use`
```json
{"event_type":"potion_use","encounter":"GREMLIN_NOB","potion":"FIRE_POTION","player":76561197983754930,"character":"IRONCLAD","target":"GREMLIN_NOB:1","turn":1,"timestamp":1776012553}
```
- `target`: player's own creature instance ID for self-targeted potions (e.g. Block Potion), monster instance ID for targeted potions (e.g. Fire Potion), `null` for AOE potions (e.g. Explosive Ampoule)

#### `orb_channeled`
Defect character only.
```json
{"event_type":"orb_channeled","encounter":"AUTOMATON","player":76561197983754930,"character":"DEFECT","orb":"LIGHTNING","turn":1,"timestamp":1776012551}
```

#### `stars_gained`
Stars-based characters only.
```json
{"event_type":"stars_gained","encounter":"JAW_WORM","player":76561197983754930,"character":"WATCHER","amount":1,"turn":1,"timestamp":1776012551}
```

---

### Enemy and relic events

#### `monster_action`
Fires after each monster performs its move. Appears between `turn_end N` and `turn_start N+1`.
```json
{"event_type":"monster_action","encounter":"GREMLIN_NOB","monster":"GREMLIN_NOB:1","move":"BASH","intents":["Attack"],"targets":["IRONCLAD_BODY:0"],"turn":1,"player":76561197983754930,"timestamp":1776012556}
```
- `move`: the move state ID the monster performed
- `intents`: `IntentType` strings confirming what happened (may differ from the `turn_start` snapshot for dynamically chosen moves)
- `targets`: creature instance IDs of targets; non-empty for most moves including buffs (the game passes player creatures as targets regardless of move type); empty only for untargeted moves like escape

#### `relic_trigger`
Fires whenever a relic flashes during combat.
```json
{"event_type":"relic_trigger","encounter":"GREMLIN_NOB","relic":"ANCHOR","player":76561197983754930,"targets":["IRONCLAD_BODY:0"],"turn":0,"timestamp":1776012549}
```
- `targets`: creature instance IDs the flash targeted (typically the owner's creature)
- `turn:0` is valid (relics that fire during `BeforeCombatStart`, e.g. Anchor, Blood Pact)

---

### Out-of-combat events

#### `rewards_offered`
Fires before the reward screen is shown. Covers all options including ones the player will skip.
```json
{
  "event_type": "rewards_offered",
  "rewards": [
    {"reward_type": "gold", "item": "25"},
    {"reward_type": "card", "item": "BASH"},
    {"reward_type": "card", "item": "CLEAVE"},
    {"reward_type": "card", "item": "ANGER"}
  ],
  "floor": 1,
  "player": 76561197983754930,
  "timestamp": 1776012561
}
```
Each card on a card reward screen is a separate entry. `item` for gold rewards is the gold amount as a string.

#### `reward_taken`
Fires when the player claims a reward.
```json
{"event_type":"reward_taken","reward_type":"card","item":null,"floor":1,"player":76561197983754930,"timestamp":1776012562}
{"event_type":"reward_taken","reward_type":"gold","item":null,"amount":25,"floor":1,"player":76561197983754930,"timestamp":1776012562}
{"event_type":"reward_taken","reward_type":"relic","item":"ANCHOR","floor":1,"player":76561197983754930,"timestamp":1776012562}
```
- `reward_type: "card"` — `item` is **always null** (which specific card was taken is not exposed at hook time; the taken card can be inferred from `rewards_offered` — whichever offered card subsequently appears in the player's deck)
- `reward_type: "gold"` — `amount` field is present; `item` is null
- `reward_type: "relic"` / `"potion"` — `item` is the model ID

Correlate `rewards_offered` + `reward_taken` on `floor` + `player` to reconstruct which options were available and which were skipped.

#### `event_choice`
```json
{"event_type":"event_choice","event":"BIG_FISH","option_key":"big_fish.options.GAIN_MAX_HP","floor":6,"player":76561197983754930,"timestamp":1776012580}
```
`option_key` is the same dotted-path string the game writes to its own `.run` history.

#### `rest_site_choice`
```json
{"event_type":"rest_site_choice","option":"HEAL","floor":10,"player":76561197983754930,"timestamp":1776012600}
```
`option` is one of: `HEAL`, `SMITH`, `DIG`, `CLONE`, `MEND`, `LIFT`, `HATCH`, `COOK`.

#### `shop_offered`
Fires once per shop floor, immediately after `room_entered`. Contains the full shop inventory.
```json
{
  "event_type": "shop_offered",
  "items": [
    {"item_type": "card", "item": "THUNDERCLAP", "cost": 75},
    {"item_type": "relic", "item": "ANCHOR", "cost": 150},
    {"item_type": "potion", "item": "FIRE_POTION", "cost": 40},
    {"item_type": "card_removal", "item": null, "cost": 75}
  ],
  "floor": 8,
  "player": 76561197983754930,
  "timestamp": 1776012620
}
```
`item_type` values: `"card"`, `"relic"`, `"potion"`, `"card_removal"`. `item` is null for `card_removal`. Every shop has exactly one `card_removal` entry. Correlate with `shop_purchase` on `floor` + `player` to find what was available but not bought.

#### `shop_purchase`
```json
{"event_type":"shop_purchase","item_type":"card","item":"THUNDERCLAP","gold_spent":75,"floor":8,"player":76561197983754930,"timestamp":1776012625}
{"event_type":"shop_purchase","item_type":"card_removal","item":null,"gold_spent":75,"floor":8,"player":76561197983754930,"timestamp":1776012630}
```

---

## Typical event sequence for a combat floor

```
room_entered        (floor N, room_type: "Monster")
combat_start        (encounter: "JAW_WORM")
turn_start          (turn: 1, players snapshot, monsters snapshot)
  card_draw × 5    (from_hand_draw: true)
  card_play         (target: "JAW_WORM:1")
  damage_dealt      (target: "JAW_WORM:1", ...)
  block_gained      (target: "IRONCLAD_BODY:0", ...)
  card_discard × N  (from_flush: true)
turn_end            (turn: 1)
monster_action      (monster: "JAW_WORM:1", move: "CHOMP", targets: ["IRONCLAD_BODY:0"])
damage_dealt        (target: "IRONCLAD_BODY:0", ...)
turn_start          (turn: 2, ...)
  ...
turn_end            (turn: K)
combat_end          (outcome: "victory")
rewards_offered     (floor N, cards + gold)
reward_taken × N    (floor N, one per claimed reward)
```

For a rest site floor:
```
room_entered        (floor N, room_type: "RestSite")
rest_site_choice    (option: "HEAL")
```

For a shop floor:
```
room_entered        (floor N, room_type: "Shop")
shop_offered        (items: [...])
shop_purchase       (if player buys something)
```

---

## Implementation notes

- **Idempotency**: the mod has no retry logic, so duplicate delivery only happens if a `run_end` is lost and the game restarts the same run (rare). A simple unique index on `(run_start_timestamp, event_sequence_number)` handles this if needed; otherwise accepting duplicates is fine for analytics.
- **Partial runs**: if the game crashes, the final drain may not complete. You may receive events up to some point mid-run with no `run_end`. Handle this gracefully — treat runs without `run_end` as in-progress or abandoned.
- **`turn:0` events**: valid and meaningful — covers combat setup before the first player turn (Defect starting orbs, relic block grants). Do not filter these out.
- **`character` field on card events**: present on `card_draw`, `card_play`, `card_discard`, `card_exhaust`, `potion_use`, `orb_channeled`, `stars_gained` — the character model ID of the acting player. Useful for character-specific analysis without joining through `run_end`.
- **Multiplayer**: `player` fields are NetId ulongs — unique per player in a co-op session. `num_players` on `run_end` indicates multiplayer runs.
