# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build, Deploy & Package

The publishing flow is split into three separable steps, each a distinct MSBuild target with a thin shell wrapper. **Deploy** and **Package** both `DependsOnTargets="Build"`, so each always acts on a freshly-compiled DLL. A plain build does **not** copy anything.

```bash
# 1. Build only — compile, no copy       (dotnet build -c Debug)
scripts/build.sh

# 2. Deploy — fresh Debug build, install into the game's mods/expanded-telemetry/
scripts/deploy.sh            # dotnet build -c Debug -t:Deploy

# 3. Package — fresh Release build, zip a release artifact to dist/
scripts/package.sh          # dotnet build -c Release -t:Package
                            # -> dist/expanded-telemetry-<version>.zip (upload to ModsNexus / GitHub release)

# Copy game log locally for inspection
scripts/fetch-log.sh
```

The scripts live in `scripts/` and resolve the project root as one level up (`$(dirname "$0")/..`). All three build scripts require the `STS2_DIR` environment variable pointing to the folder containing `sts2.dll` (the `Build` they each depend on needs it to resolve the `sts2` reference). Set it once in your shell profile — the `.csproj` reads it directly via MSBuild's env var support and errors with a clear message if unset. Any extra args pass through to `dotnet build` (e.g. `scripts/deploy.sh -c Release`).

- **`Deploy`** copies the mod payload (DLL, `.deps.json`, `.runtimeconfig.json`, `.pdb` in Debug, `mod_manifest.json`, plus `mod_image.png`/`.pck` if present) to `mods/expanded-telemetry/`. The mods path is derived from `STS2_DIR` (macOS `.app`: `Contents/MacOS/mods`; Win/Linux: next to the game exe).
- **`Package`** stages the same payload under a top-level `expanded-telemetry/` folder and zips it to `dist/expanded-telemetry-<version>.zip`, so extracting the zip into a `mods/` directory yields `mods/expanded-telemetry/`. The `<version>` is read from `mod_manifest.json` (single source of truth — bump it there). Release builds emit no `.pdb`, so the release zip ships without symbols. `dist/` is gitignored.
- The shared `_CollectModPayload` target defines the exact file set once; both `Deploy` and `Package` reuse it.

Hot-reload via the MCP watcher is currently **broken** (pending a rework of the in-game bridge/remote-control mod) — deploy manually with `scripts/deploy.sh` and restart the game for now.

## Architecture

**expanded-telemetry** is a Slay the Spire 2 mod that streams gameplay telemetry as NDJSON to disk in real-time. It is purely observational (`affects_gameplay: false`).

### Core Modules (`Code/`)

| File | Purpose |
|------|---------|
| `ModEntry.cs` | `[ModInitializer]` entry point — loads `TelemetryConfig`, initializes Harmony, calls `PatchAll()` |
| `TelemetryConfig.cs` | Self-contained JSON config reader; writes defaults on first run; read once at mod init |
| `TelemetryStreamWriter.cs` | Routes events to file and/or remote; `_isOpen` flag guards idempotent `Open()`; contains `RewardSummary` + `ShopItemSummary` records |
| `RemoteSender.cs` | Fire-and-forget background HTTP sender; `ConcurrentQueue` drained every 200ms; 2000-event bound; 3s timeout; drops on failure |
| `EncounterCardTracker.cs` | Maps `CombatState → encounterID` via `ConditionalWeakTable`; dispatches events to the writer |
| `Patches/CombatPatches.cs` | 18 Harmony patches that hook game events and call into `EncounterCardTracker` |
| `Patches/OutOfCombatPatches.cs` | 7 Harmony patches for out-of-combat events — call `TelemetryStreamWriter` directly (no tracker layer needed) |

### Event Flow

**Out-of-combat (OutOfCombatPatches.cs):**
- `AfterRoomEnteredPatch` → opens stream (idempotent) + writes `room_entered` for every floor/room + writes `shop_offered` inline when entering a Shop room
- `RestSiteChoicePatch` → writes `rest_site_choice` when player picks a rest option
- `EventChoicePatch` → writes `event_choice` when player picks an event option
- `ShopPurchaseCapturePatch` + `ShopPurchasePatch` → capture item ID before `ClearAfterPurchase`, write `shop_purchase`
- `RewardsOfferedPatch` → writes `rewards_offered` listing all reward options before screen resolves
- `RewardTakenPatch` → writes `reward_taken` when player claims any reward

**Combat (CombatPatches.cs):**
1. `BeforeCombatStartPatch` → opens stream (idempotent) + registers combat
2. `BeforeSideTurnStartPatch` (player side only) → write `turn_start` (with player+monster snapshot), increment turn counter
3. `CardDrawnPatch` / `CardPlayStartedPatch` / `CardChangedPilesPatch` / `CardExhaustedPatch` / `PotionUsedPatch` → lookup encounter ID + turn number → write event
4. `AfterPowerAmountChangedPatch` / `AfterDamageReceivedPatch` / `AfterBlockGainedPatch` / `AfterOrbChanneledPatch` / `AfterStarsGainedPatch` → write combat resource events
4b. `MonsterPerformedMovePatch` → write `monster_action` (fires during enemy turn, between player `turn_end` and next `turn_start`)
4c. `RelicFlashPatch` → write `relic_trigger` whenever a relic flashes (guarded to combat-only)
5. `AfterTurnEndPatch` (player side only) → write `turn_end`
6. `AfterCombatEndPatch` / `LoseCombatPatch` → write `combat_end`, remove combat from table
7. `CreateRunHistoryEntryPatch` → write `run_end`, close stream, rename file

### Output Files

- **In-progress**: `{run_id}.in_progress.expanded_run` (AutoFlush=true, crash-safe; named with `run_id` so resume after process restart appends to the correct file)
- **Finalized**: `{run_id}.expanded_run` (renamed on run end)
- **Location**: `{OS.GetUserDataDir()}/expanded_telemetry/{Steam64}/profile{N}/` (e.g. `~/Library/Application Support/SlayTheSpire2/expanded_telemetry/{Steam64}/profile1/`). Deliberately **outside** the game's `saves/history/` directory — see the cloud-store note under Key Design Decisions.

### Event Types (NDJSON)

`run_start`, `room_entered`, `combat_start`, `turn_start`, `card_draw`, `card_play`, `card_discard`, `card_exhaust`, `potion_use`, `power_applied`, `damage_dealt`, `block_gained`, `orb_channeled`, `stars_gained`, `relic_trigger`, `monster_action`, `turn_end`, `combat_end`, `rewards_offered`, `reward_taken`, `event_choice`, `rest_site_choice`, `shop_offered`, `shop_purchase`, `run_end`

Each event includes `event_id` (string, v4 UUID — a fresh `Guid.NewGuid()` (via the `NewEventId` helper) stamped as the first field on every event object; globally unique, use as the backend dedup key), `player_id` (string, **v5 UUID** — `TelemetryId.Player(localSteam64)`, a deterministic pseudonym of the **local player's** Steam64; the raw Steam id never leaves the machine), `run_id` (string, **v5 UUID** — `TelemetryId.Run(startTime)`, derived from the run's start time; see below), `seq_num` (long, per-run monotonic counter stamped in emission order — see ordering note), and `timestamp` (integer Unix seconds UTC). The `(player_id, run_id)` tuple uniquely identifies a run; `event_id` uniquely identifies a single event line. The `run_start` event is written when the stream is first opened (on the first `room_entered` or `combat_start` of a run); on resume it is skipped and the mod appends to the existing file. **Field names match the ingest service's `Event` struct** (`sts2-telemetry-ingest`): `event_id`, `event_type`, `player_id`, `run_id`, `timestamp`, and `seq_num` are top-level typed columns (`seq_num INT NOT NULL`, added in migration `20260811052224`); every other field (`encounter`, `card`, …) is flattened into a `data` JSONB column.

**Player/run ids are v5 UUIDs** (`Code/TelemetryId.cs`): RFC 4122 name-based (SHA-1). `player_id = uuid5(PlayerNamespace, localSteam64.ToString())`, `run_id = uuid5(RunNamespace, startTime.ToString())`. Two fixed, distinct namespace constants (do **not** change — either change repartitions all previously-emitted ids). **`player_id` is always the LOCAL player** (`PlatformUtil.GetLocalPlayerId`), on every event. Do **not** use `Player.NetId` for identity: `NetId` is a per-session lobby id (the local player is `1` in single-player), **not** the Steam64 — an earlier version mixed the two and produced inconsistent `player_id`s within a run. (There is currently no per-actor player attribution in multiplayer; every event is stamped with the local player. If that's needed later, add a separate non-PII `net_id` field — `Player` exposes only `NetId`, no Steam64 accessor.) The raw start-time long is still used internally for file naming/resume. Verified byte-for-byte against reference `uuid5` (Python/Postgres/Rust). `run_id` derives from the start-time alone, so two *different* players starting in the same second share a `run_id` UUID — fine, because runs are keyed by the `(player_id, run_id)` pair.

**Ordering (`seq_num`)**: `timestamp` is integer-seconds only (the ingest deserializes it with `ts_seconds`), so same-second events can't be ordered by timestamp alone. Every event carries `seq_num`, a per-run counter (`NextSeq()`) incremented in emission order. Fresh runs start at 0; **on resume `Open()` recovers the last `seq_num`** from the existing file (`RecoverLastSeq`, reads the last line) so it stays monotonic across a save+quit → Continue. Downstream orders a run by `seq_num` alone. (Fallback: recovery needs `WriteToFile=true`; in server-only mode a resume restarts `seq_num` at 0, so order by `(timestamp, seq_num)` there.)

Creature instance IDs (`target`, `dealer`, `applier`, `monster`, `monsters[].id`, `targets[]`) use `"{ModelId}:{CombatId}"` format (e.g. `"GREMLIN_NOB:2"`). `CombatId` is a `uint?` assigned sequentially by `CombatState.AttachCreature()`, unique per combat. See `TelemetryStreamWriter.CreatureId(Creature)`.

### Event Fields

| Event | Extra fields |
|-------|-------------|
| `run_start` | `game_version` (string — from `ReleaseInfoManager.Instance.ReleaseInfo?.Version`, falls back to `"dev"` for local builds), `profile` (int, 1–3 — `SaveManager.Instance.CurrentProfileId`), `ascension` (int — `RunManager.Instance.ToSave(null).Ascension`, same source as `run_end.ascension`), `character` (string — local player's `CharacterId.Entry`, resolved from `runSave.Players` the same way `run_end` does; `""` if unset), `num_players` (int — `runSave.Players.Count`, same source as `run_end.num_players`) |
| `room_entered` | `room_type` (RoomType enum string), `floor` (int), `act` (int, 1-based), `player_id` (v5 UUID) |
| `combat_start` | `encounter`, `player_id` |
| `turn_start` | `encounter`, `turn`, `player_id`, `players` (array — each: `player_id` (v5 UUID), `character`, `hp`, `max_hp`, `block`, `energy`, `max_energy`, `powers[]` `{power, amount}`), `monsters` (array — each: `id` (creature instance ID), `hp`, `max_hp`, `block`, `powers[]` `{power, amount}`, `intents[]` `{type}`) |
| `card_draw` | `encounter`, `card`, `player_id`, `character`, `from_hand_draw` (bool — true = start-of-turn draw, false = effect-triggered), `turn`, `upgrade_level` (int — 0 = base, 1 = upgraded) |
| `card_play` | `encounter`, `card`, `player_id`, `character`, `target` (creature instance ID or null for untargeted), `turn`, `upgrade_level`, `is_auto_play` (bool — true = triggered by power/relic, false = played by player; **needs manual verification**) |
| `card_discard` | `encounter`, `card`, `player_id`, `character`, `from_flush` (bool — true = end-of-turn hand flush, false = explicit/effect discard), `turn`, `upgrade_level` |
| `card_exhaust` | `encounter`, `card`, `player_id`, `character`, `from_ethereal` (bool — true = Ethereal keyword auto-exhaust at end of turn, false = explicit card/power effect), `turn`, `upgrade_level` |
| `potion_use` | `encounter`, `potion`, `player_id`, `character`, `target` (creature instance ID, player's own for self-targeted, null for untargeted/AOE), `turn` |
| `power_applied` | `encounter`, `power` (PowerModel id), `target` (creature instance ID), `applier` (nullable creature instance ID — null when power self-decrements), `amount` (int, negative for stack reductions), `turn`, `player_id` |
| `damage_dealt` | `encounter`, `target` (creature instance ID), `dealer` (nullable creature instance ID), `hp_lost` (int), `blocked` (int), `overkill` (int), `turn`, `player_id` |
| `block_gained` | `encounter`, `target` (creature instance ID), `amount` (int), `turn`, `player_id` |
| `orb_channeled` | `encounter`, `player_id` (v5 UUID), `character`, `orb` (OrbModel id), `turn` — Defect only |
| `stars_gained` | `encounter`, `player_id` (v5 UUID), `character`, `amount` (int), `turn` — stars characters only |
| `relic_trigger` | `encounter`, `relic` (RelicModel id), `player_id` (v5 UUID, relic owner), `targets` (list of creature instance IDs the flash targeted), `turn` |
| `monster_action` | `encounter`, `monster` (creature instance ID), `move` (MoveState id), `intents` (list of IntentType strings), `targets` (list of creature instance IDs, empty if untargeted), `turn`, `player_id` |
| `rewards_offered` | `rewards` (array of `{reward_type, item}` — `reward_type`: `"card"` / `"relic"` / `"potion"` / `"gold"`; `item`: card/relic/potion ID or gold amount string), `floor`, `player_id` |
| `reward_taken` | `reward_type` (`"card"` / `"relic"` / `"potion"` / `"gold"`), `item` (ID string — null only for `gold` and skipped card rewards), `amount` (int — present only on `gold`), `floor`, `player_id` |
| `event_choice` | `event` (EventModel id), `option_key` (TextKey string of the chosen option), `floor`, `player_id` |
| `rest_site_choice` | `option` (OptionId string: `"HEAL"` / `"SMITH"` / `"DIG"` / `"CLONE"` / `"MEND"` / `"LIFT"` / `"HATCH"` / `"COOK"`), `floor`, `player_id` |
| `shop_offered` | `items` (array of `{item_type, item, cost}` — `item_type`: `"card"` / `"relic"` / `"potion"` / `"card_removal"`; `item`: ID string or null for `card_removal`; `cost`: int gold price), `floor`, `player_id` |
| `shop_purchase` | `item_type` (`"card"` / `"relic"` / `"potion"` / `"card_removal"`), `item` (ID string or null for `card_removal`), `gold_spent` (int), `floor`, `player_id` |
| `run_end` | `win`, `abandoned`, `character`, `ascension`, `num_players`, `player_id` |

## Key Design Decisions

- **`ConditionalWeakTable`** for combat tracking: prevents memory leaks + supports simultaneous multiplayer combats
- **Streaming writes with `AutoFlush=true`**: every event is on disk immediately — no data loss on crash
- **Graceful error handling**: all write errors are logged but not thrown — mod never disrupts gameplay
- **`run_start` is implicit**: emitted inside `OnCombatStart` when the stream isn't yet open, not from a dedicated hook
- **`card_discard` patches `Hook.AfterCardChangedPiles`** (not `CombatHistory.CardDiscarded`): end-of-turn flush discards bypass `CombatHistory.CardDiscarded` entirely, going straight through `CardPileCmd.Add`. `AfterCardChangedPiles` fires for both paths. Filter: `oldPile == Hand && card.Pile.Type == Discard`.
- **Each patch uses the hook that carries the relevant semantic data**: `CombatHistory.CardDrawn` carries `fromHandDraw`; `CombatHistory.CardPlayStarted` carries the `CardPlay` object with target; `AfterCardChangedPiles` is used for discards specifically because it's the only hook that covers all discard paths; `Hook.AfterCardExhausted` carries `causedByEthereal` directly.
- **`card_exhaust` patches `Hook.AfterCardExhausted`**: single call site in `CardCmd.ExhaustCard`, covers all exhaust paths. The `causedByEthereal` parameter maps to `from_ethereal` in the event.
- **`potion_use` patches `Hook.AfterPotionUsed`**: guards `combatState == null` to skip out-of-combat uses. Target follows the same nullable pattern as `card_play` — `target?.ModelId.Entry`.
- **`combat_end` outcome**: `Hook.AfterCombatEnd` only fires on the victory path (`EndCombatInternal`). Defeat is detected via a separate Prefix on `CombatManager.LoseCombat` (which fires once when defeat is registered, before `ProcessPendingLoss` tears down state). `CombatManager.Instance.IsAboutToLose` guards against duplicate calls.
- **`turn_start` patches `Hook.BeforeSideTurnStart`** (not `AfterPlayerTurnStart`): `AfterPlayerTurnStart` fires *after* `CardPileCmd.Draw` in `SetupPlayerTurn`, so start-of-turn draws would be tagged with the previous turn number. `BeforeSideTurnStart` fires before draws. Filter: `side == CombatSide.Player`. The snapshot (`players` + `monsters`) is taken at this same moment — post-monster-turn, pre-draw, which is the "state entering the player's decision" baseline.
- **Monster intent snapshot uses `NextMove.Intents`**: by `BeforeSideTurnStart` time, enemies have already rolled their next move. Empty `intents[]` means the monster has no move queued yet (e.g. spawned this turn). Intent types are serialized as the `IntentType` enum name (e.g. `"Attack"`, `"Buff"`, `"Debuff"`, `"Defend"`, `"Hidden"`). No `turn_end` snapshot — it's redundant given the delta event stream.
- **`turn_end` patches `Hook.AfterTurnEnd`** filtered to `CombatSide.Player`: fires after the end-of-turn flush in `EndPlayerTurnPhaseTwoInternal`, so flush discard events are bracketed inside the correct turn.
- **Turn counter** is a `TurnCounter` class (mutable int wrapper) stored in a second `ConditionalWeakTable<ICombatState, TurnCounter>`. Starts at 0, incremented to 1 on the first `BeforeSideTurnStart`. Not explicitly removed on combat end — the `ConditionalWeakTable` GC-evicts it (and the encounter-id map) once the combat state is collected. `OnCombatEnd` used to remove both eagerly, but that dropped post-victory relic triggers (see `relic_trigger` note); it now keeps the mappings and guards `combat_end` against double-emit via a third `_ended` CWT.
- **`power_applied` patches `Hook.AfterPowerAmountChanged`**: fires for both gains (positive `amount`) and reductions (negative `amount`, e.g. Poison ticking down). Covers all creatures — player and monsters. `applier` is null when a power self-decrements. Creature IDs use `ModelId.Entry` (string) to be consistent with `potion_use` target.
- **`damage_dealt` patches `Hook.AfterDamageReceived`**: fires for every damage instance across all creatures. `combatState` is nullable — guarded. `hp_lost` / `blocked` / `overkill` come directly from `DamageResult` fields. Fires for both player-takes-damage and monster-takes-damage.
- **`block_gained` patches `Hook.AfterBlockGained`**: fires for all block gains including relic-granted values at combat start (e.g. 999 from Plating in AutoSlay).
- **`orb_channeled` patches `Hook.AfterOrbChanneled`**: Defect-specific; fires at `turn:0` for start-of-combat passive channels (e.g. GalvanicPower via `BeforeCombatStart`). Like every event, `player_id` is the local player's v5 UUID.
- **`stars_gained` patches `Hook.AfterStarsGained`**: character-specific (Watcher/stars characters). Will produce no events in a Defect run — this is expected.
- **`relic_trigger` patches `RelicModel.Flash(IEnumerable<Creature> targets)`**: the overload with targets is the one all activations route through (the no-arg `Flash()` delegates to it). `Flash` is called in non-combat contexts too (card reward screen, shop, treasure room) — guarded by `CombatManager.Instance.DebugOnlyGetState() == null`. `targets` are the creatures passed to `Flash`; no-arg `Flash()` passes just the owner's creature, so single-target relics emit a one-element list. `player_id` is the local player's v5 UUID (like every event). **Post-victory relics** (e.g. Burning Blood's end-of-combat heal) flash in `Hook.AfterCombatVictory`, which fires *after* `Hook.AfterCombatEnd` — so their `relic_trigger` appears in the stream *after* that combat's `combat_end`. This requires the encounter mapping to survive past `combat_end`; see the turn-counter note for why the mapping is no longer removed in `OnCombatEnd`.
- **Out-of-combat patches call `TelemetryStreamWriter` directly** (no `EncounterCardTracker` layer): out-of-combat events need no per-combat state (no `ConditionalWeakTable`), so the tracker is bypassed. `Open()` is called at the top of each out-of-combat patch — idempotent, so safe on every room entry.
- **`room_entered` patches `Hook.AfterRoomEntered`**: fires for all room types including combat rooms (redundant with `combat_start` but gives a clean floor-by-floor traversal log). `runState.TotalFloor` = floor number; `runState.CurrentActIndex + 1` = act. `room_type` is the `RoomType` enum name.
- **`rest_site_choice` uses `[HarmonyPatch]` + `TargetMethods()`** (not `[HarmonyPatch(typeof(RestSiteOption), ...)]`): `RestSiteOption.OnSelect()` is abstract — Harmony throws `"Abstract methods cannot be prepared"` if patched directly, which aborts `PatchAll()` and silently disables all subsequent patches. `TargetMethods()` discovers all non-abstract subclasses in the assembly at patch time, patching each concrete `OnSelect()` override instead. `OptionId` is public on each subclass (`"HEAL"`, `"SMITH"`, `"DIG"`, etc.). `Owner` is `protected` — accessed via a cached `PropertyInfo` reflection call.
- **`event_choice` patches `EventSynchronizer.ChooseOptionForEvent(Player, int)` (private method)**: referenced by name string in `[HarmonyPatch]`. `GetEventForPlayer(player)` is public and returns the active `EventModel`; `eventModel.CurrentOptions[optionIndex].TextKey` gives the same option key the game writes to its own history. Wrapped in try-catch since the private method patch could silently fail on game updates.
- **`shop_purchase` uses a two-patch pattern**: `MerchantEntry.ClearAfterPurchase()` runs before `Hook.AfterItemPurchased` fires, nulling item models. A Prefix on `MerchantEntry.OnTryPurchaseWrapper` captures item type + ID into a `ConditionalWeakTable<MerchantEntry, string>` before the async body runs. The `AfterItemPurchased` Postfix reads and removes from the table. `MerchantCardRemovalEntry` overrides `OnTryPurchaseWrapper` entirely (bypassing the base Prefix), so it is detected by type check in the Postfix with no item ID needed.
- **`shop_offered` is emitted inside `AfterRoomEnteredPatch`** (not a separate patch): `MerchantInventory.CreateForNormalMerchant` runs before `Hook.AfterRoomEntered` in `MerchantRoom.Enter`, so patching the factory directly caused `shop_offered` to appear before `room_entered` in the stream. Fix: enumerate `merchantRoom.GetLocalInventory()` inside `AfterRoomEnteredPatch` immediately after writing `room_entered`, guaranteeing correct NDJSON order from within the same method. (The Aug 2026 update made shops per-player — `MerchantRoom.Inventory` became `Inventories` (list) + `GetLocalInventory()`; `MerchantInventory.CardEntries` still exists as a convenience over the new `CharacterCardEntries` + `ColorlessCardEntries` split.) By `AfterRoomEntered` time, all inventory data is fully populated (cards via `MerchantCardEntry.Populate()`, relics/potions via constructors).
- **`rewards_offered` patches `Hook.ModifyRewards`** (was `Hook.BeforeRewardsOffered`, removed in the Aug 2026 update): `ModifyRewards(IRunState, Player, List<Reward> rewards, AbstractRoom?)` is a synchronous hook fired from `RewardsSet.GenerateWithoutOffering()` after the reward list is built and reward-modifiers have mutated it in place, so the Postfix sees the final list. **Caveat:** `GenerateWithoutOffering()` also runs on the non-offering paths (`RewardsCmd.GenerateForRoomEnd` / `GenerateCustom`), so this can in principle fire for rewards never shown; normal combat/treasure rewards go `OfferForRoomEnd → Offer → GenerateWithoutOffering`, firing once per screen. `CardReward.Cards` and `PotionReward.Potion` are public. `RelicReward._relic` is private — accessed via a cached `FieldInfo` reflection. Gold rewards use `GoldReward.Amount` (public). Each card in a `CardReward` becomes a separate entry in the `rewards` array. Combined with `reward_taken`, consumers can reconstruct "offered but not picked" for every reward screen.
- **`reward_taken` patches `Hook.AfterRewardTaken`**: `RelicReward.ClaimedRelic` and `PotionReward.ClaimedPotion` are set during `OnSelect()` and are accessible by the time the hook fires. For `CardReward`, `CardReward.Cards` is a live LINQ projection over `_cards`; `OnSelect()` calls `_cards.RemoveAll(c => c.Card == result)` for the chosen card before `AfterRewardTaken` fires. `RewardsOfferedPatch` snapshots the full card ID list into a `ConditionalWeakTable<CardReward, List<string>>`; `RewardTakenPatch` diffs the snapshot against the post-pick `Cards` to recover the taken card ID. If the player skips (no card removed), `item` is null. Gold rewards include `amount` instead of `item`.
- **`monster_action` patches `CombatHistory.MonsterPerformedMove`**: called at the end of `MonsterModel.PerformMove()` after the move resolves — same pattern as `CombatHistory.CardPlayStarted`. Fires during the enemy turn (between player `turn_end` N and `turn_start` N+1), so events carry `turn: N`. `move` is `MoveState.StateId` (the move name string, e.g. `"BASH"`). `intents` are the `IntentType` strings from the move's intent list — confirms what actually happened. `targets` is the list of creature `ModelId.Entry` values (`CombatHistory.MonsterPerformedMove` passes `targets` as the player creatures; null targets become an empty list).
- **`run_id` derives from `RunManager.Instance.ToSave(null).StartTime`**: a `long` Unix-seconds value set at run creation and **persisted in the save file** (`current_run.save` → `start_time`), reloaded into `RunManager._startTime` on every Continue — so it is **stable across a graceful save+quit → Continue**, not just process restarts (`_sessionStartTime` is a separate field that does reset each load). Captured once in `Open()` as the raw `_runId` long (used for file naming), then emitted as the v5-UUID `run_id` on every event.
- **Robust resume (`Open()`)**: the raw `_runId` names the on-disk file. A hard crash leaves `{run_id}.in_progress.expanded_run`; a graceful save+quit runs `CreateRunHistoryEntry → Finalize`, which renames it to the finalized `{run_id}.expanded_run` **and** writes a (possibly `abandoned:true`) `run_end`. On the next `Open()` for the same `run_id`, resume is detected from **either** file — if only the finalized one exists it is renamed back to `.in_progress` — then the mod appends (never overwrites), recovers `seq_num` (`RecoverLastSeq`), and skips re-emitting `run_start`. **Consequence:** a save+quit → Continue run accumulates one intermediate `run_end` per quit; **downstream should treat the max-`seq_num` `run_end` as terminal** and ignore earlier ones. This replaced an earlier bug where resume only looked for `.in_progress`, so a Continue re-emitted `run_start`, reset `seq_num`, and `Finalize`'s `File.Move(overwrite:true)` clobbered the prior file. **Crucially, resume only works because the files now live outside the cloud-synced dir** (see next bullet); when they were in `saves/history/`, the prior file was deleted between sessions so resume could never find it.
- **Telemetry files live OUTSIDE the game's `saves/history/` directory** (`GetHistoryFilePath` uses `OS.GetUserDataDir()`, the same base as the config). That history dir is backed by a `CloudSaveStore` whose sync is cloud-authoritative: on each sync, any file present locally but not in Steam Cloud is **deleted** (`CloudSaveStore.SyncCloudToLocalInternal` → `LocalStore.DeleteFile(path)`), and a 5 MB/100-file quota pruner (`ForgetFilesInDirectoryBeforeWritingIfNecessary`) forgets the oldest cloud-persisted files. The game's own `.run` history files survive because it writes them *through* the cloud store (so they exist in the cloud); the mod writes via a raw `StreamWriter`, so its files are local-only and were silently deleted between a save+quit and the resume. `ShouldSyncFileToCloud` = `!name.EndsWith(".backup")`, i.e. everything non-`.backup` is cloud-managed. Writing under `OS.GetUserDataDir()` (not cloud-managed) makes the files durable — which is what lets robust resume actually work.
- **Multi-output routing via `_isOpen` + `_writeToFile` + `_sendToServer` flags**: all three are captured once in `Open()` from `ModEntry.Config` and held for the duration of the run. `WriteEvent()` serializes the event to JSON once, then writes to the file writer (if enabled) and/or enqueues to `RemoteSender` (if enabled). Config changes mid-run are not reflected until the next run.
- **Remote streaming is fire-and-forget**: `RemoteSender` drains a `ConcurrentQueue<string>` in a background `Task.Run` loop every 200ms, POSTing NDJSON batches to the configured URL. The queue is bounded at 2000 events; overflow is dropped with a log warning. Game thread only calls `Enqueue()` (O(1), never blocks). `Finalize()` cancels the drain loop and calls `RemoteSender.Flush(3000)` — best-effort 3s final drain. **Failed sends drop the batch and log with a categorized reason** — `PostAsync` does not throw on non-2xx, so `SendBatch` inspects `resp.IsSuccessStatusCode` and classifies: `401`/`403` → "auth rejected … check AuthToken/ServerUrl", `>=500` → "server error", other non-2xx → "rejected", and a thrown exception → "network error". Logging is throttled (`NoteFailure`/`NoteSuccess`): the first failure of a given kind logs immediately, then only every 100th consecutive same-kind failure (with an `(xN)` suffix), so a persistent bad token / down server can't spam the log at the 200ms cadence; a successful send resets the streak.
- **`TelemetryConfig` is a self-contained JSON file** (no BaseLib or in-game UI): written to `{OS.GetUserDataDir()}/mod_configs/expanded-telemetry.cfg` on first load with defaults; loaded once in `ModEntry.Init()`. `OS.GetUserDataDir()` is safe to call at init time (Godot is up by then). Fields: `WriteToFile`, `SendToServer`, `ServerUrl`, `AuthToken`. If `SendToServer=true` and `ServerUrl` is empty, an error is logged and remote output is disabled for that run. The mod only writes the file when it is absent — it never rewrites an existing file, so new fields (e.g. `AuthToken`) must be added by hand to a pre-existing config (missing fields deserialize to their defaults). Edits are picked up on the next game launch (config is read once per process).
- **Remote auth via `AuthToken`**: when non-empty, `RemoteSender.Start(serverUrl, authToken)` sets `_http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken)` on the shared singleton `HttpClient`, so every batch POST carries `Authorization: Bearer <token>`. Passing an empty/whitespace token sets the header to `null`, clearing any token from a prior run. The token is stored in plaintext in the config file (game user-data dir, outside the repo) and never logged (the config load line logs only `(set)`/`(unset)`).
- **`STS2_DIR` environment variable** drives all build tooling: the `.csproj` reads `$(STS2_DIR)` directly as an MSBuild property (MSBuild promotes all env vars). `Sts2Dir` is set from `$(STS2_DIR)` if unset, then used for `sts2.dll` hint path and deriving the mods deploy path. A `<Error>` target fires with a clear message if unset. No `local.props` or other indirection needed.
- **BaseLib (`Alchyr.Sts2.BaseLib`) was removed**: attempted for in-game config UI, but the game's assembly load context doesn't probe the mod folder for transitive dependencies, causing `ReflectionTypeLoadException` on load. The `[ModuleInitializer]`-based resolver hook was also tried but abandoned. The self-contained JSON config is simpler and has no external dependency.

## Testing

Use the `sts2-runtime-tester` agent (defined in `.claude/agents/sts2-runtime-tester.md`) to deploy and verify against the live game. Use a **slow poll cadence** when waiting for AutoSlay runs — check status every 2-3 minutes, not in a tight loop.

Key invariants:
- `run_start` is always the first event
- Every `combat_start` is followed by a `combat_end`
- `run_end` is always the last event and includes `win`/`abandoned`/`character`/`ascension`/`num_players`
- Targeted card plays have a non-null `target`; untargeted ones are `null`
- `card_draw` events have `from_hand_draw` bool; ~89% true in typical runs
- `card_discard` events have `from_flush` bool; majority true in typical runs, false for effect-triggered discards (e.g. Acrobatics, Calculated Gamble, boss mechanics like Test Subject)
- `card_exhaust` events have `from_ethereal` bool; majority false (explicit exhaust) in typical runs, true for Ethereal cards like DAZED
- `combat_end` has `outcome: "victory"` or `"defeat"`; AutoSlay runs will only produce `"victory"` — defeat must be verified manually
- `potion_use` events have a nullable `target`; null for AOE/untargeted potions, player's own ID for self-targeted (e.g. Block Potion), monster ID for targeted (e.g. Fire Potion)
- All card events (`card_draw`, `card_play`, `card_discard`, `card_exhaust`) have `upgrade_level >= 0`; typically ~80% are 0 (base), ~20% are 1 (upgraded) in a mid-run sample
- `is_auto_play` is present on all `card_play` events; AutoSlay sets this to `true` for every play — **`false` requires manual verification** by playing a card as a human player
- Every combat contains `turn_start`/`turn_end` pairs; turn numbers start at 1 and increment by 1 each turn with no gaps
- **`turn:0` is intentional** — events that fire during `BeforeCombatStart` (before the first `BeforeSideTurnStart`) land at turn:0. This includes the Defect's starting orb channel, relic block grants (e.g. Anchor), and any power/damage effects from combat-start hooks. It is a meaningful "combat setup" phase distinct from turn 1. `AfterPlayerTurnStart` was tried as an alternative but does not fire in a way Harmony can patch and would tag turn 2+ draws with the wrong turn number anyway.
- All `card_draw`, `card_play`, `card_discard`, `card_exhaust`, `potion_use` events have `turn >= 0`; turn:0 = combat setup phase, turn:1+ = player turns. Card events within a `turn_start N` / `turn_end N` window all carry `turn: N`
- `turn_start` appears before any card draws for that turn (including start-of-turn hand draws)
- Every `turn_start` has non-empty `players` array and non-empty `monsters` array (filtered to alive enemies with `IsMonster`); `monsters[].intents` may be empty for newly spawned enemies
- `turn_start` player block is typically 0 (block resets each turn); monster block at turn start reflects whatever block they carried from their own turn (most often 0 unless they defended)
- `power_applied` events have a string `power` id, string `target`, nullable string `applier`, and int `amount`; `amount` is negative when a power self-decrements (e.g. Poison tick)
- `damage_dealt` events have string `target`, nullable string `dealer`, and non-negative int fields `hp_lost`, `blocked`, `overkill`; fires for both player and monster damage
- `block_gained` events have string `target` and positive int `amount`; fires for all creatures including large relic-granted values
- `orb_channeled` events appear only in Defect runs; first channel of the run will be at `turn:0` if the Defect has a starting-orb power
- `stars_gained` events appear only in stars-character runs; absent in Defect runs is expected
- `relic_trigger` events can appear anywhere within a combat (turn:0 through final turn); volume varies by relic loadout (~2–5 per combat typical); `targets` is always the owner's creature for all observed relics; `player_id` matches the relic owner; `turn:0` is valid for relics that fire during `BeforeCombatStart` (e.g. BLOOD_PACT, ANCHOR)
- `room_entered` appears before every `combat_start` on combat floors; `room_type` is one of `Monster`, `Elite`, `Boss`, `Event`, `Shop`, `RestSite`, `Treasure`; `floor` increments monotonically across the run; `act` is 1, 2, or 3
- `rest_site_choice` `option` is one of: `HEAL`, `SMITH`, `DIG`, `CLONE`, `MEND`, `LIFT`, `HATCH`, `COOK`; appears after a `room_entered` with `room_type: "RestSite"`
- `event_choice` `event` is a non-empty model ID (e.g. `"NEOW"`, `"BIG_FISH"`); `option_key` is a non-empty dotted path string (e.g. `"NEOW.pages.INITIAL.options.ARCANE_SCROLL"`); appears after a `room_entered` with `room_type: "Event"`
- `shop_offered` appears once per shop floor, after `room_entered` with `room_type: "Shop"`; `items` array contains all cards, relics, potions, and exactly one `card_removal` entry; `cost` > 0 for all entries; `item_type` distribution per shop is typically ~5 cards + 1-2 relics + 1-2 potions + 1 card_removal; all card/relic/potion `item` fields are non-null; correlate with `shop_purchase` on `floor` + `player_id` to find what was available vs. bought
- `shop_purchase` `item_type` is one of `card`, `relic`, `potion`, `card_removal`; `item` is non-null for card/relic/potion, null for card_removal; `gold_spent` > 0; appears after a `room_entered` with `room_type: "Shop"`
- `rewards_offered` appears after each combat before rewards are claimed; one event per combat; `rewards` array contains gold entry + 3 card entries typically; each card on the reward screen is a separate entry
- `reward_taken` appears when player accepts a reward; `reward_type: "card"` has non-null `item` with the card ID (null only if the player somehow skips without picking); `reward_type: "gold"` has `amount` field and no `item`; `reward_type: "relic"` / `"potion"` have non-null `item`
- `monster_action` events appear between `turn_end N` and `turn_start N+1`; each living enemy in the encounter produces one per enemy turn; `intents` matches the move's intent type(s); `targets` is non-empty for all observed move types including buffs/sleep (the game always passes player creatures as targets to `MonsterPerformedMove` regardless of move type); multi-intent moves (e.g. `["Attack","Debuff"]`) are correctly serialized; empty `intents` is valid for moves like `DEAD_MOVE` on dead segments

## Sample Telemetry Files

`logs/1776012547.expanded_run` — Defect run (win, 23 combats). Contains both `from_flush: true` and `from_flush: false` discard events; the false events occur during TEST_SUBJECT_BOSS which forces a full hand discard at combat start.
