# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Deploy

```bash
# Build and auto-install DLL to game mods folder
./deploy.sh                  # runs: dotnet build -c Debug

# Copy game log locally for inspection
./fetch-log.sh
```

Requires the `STS2_DIR` environment variable pointing to the folder containing `sts2.dll`. Set it once in your shell profile — the `.csproj` reads it directly via MSBuild's env var support and errors with a clear message if unset.

The `.csproj` has a `CopyToModsFolderOnBuild` MSBuild target that automatically copies the compiled DLL and supporting files to the game's `mods/expanded-telemetry/` folder (derived from `STS2_DIR`).

Hot-reload is available via MCP: use `watch_project` with `sts2-mcp-watch.json` config (1.5s debounce, auto-reload on file save).

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
- **Location**: `~/Library/Application Support/SlayTheSpire2/steam/{UserID}/modded/profile{N}/saves/history/`

### Event Types (NDJSON)

`run_start`, `room_entered`, `combat_start`, `turn_start`, `card_draw`, `card_play`, `card_discard`, `card_exhaust`, `potion_use`, `power_applied`, `damage_dealt`, `block_gained`, `orb_channeled`, `stars_gained`, `relic_trigger`, `monster_action`, `turn_end`, `combat_end`, `rewards_offered`, `reward_taken`, `event_choice`, `rest_site_choice`, `shop_offered`, `shop_purchase`, `run_end`

Each event includes `event_id` (string, v4 UUID — a fresh `Guid.NewGuid()` (via the `NewEventId` helper) stamped as the first field on every event object; globally unique, use as the backend dedup key), `player` (Steam64 ulong = `NetId`, stable per account, captured via `PlatformUtil.GetLocalPlayerId` at `Open()`), `run_id` (long, Unix seconds — `RunManager.Instance.ToSave(null).StartTime`, stable across process restarts for the same run), and `timestamp` (Unix seconds UTC). The `(player, run_id)` tuple uniquely identifies a run; `event_id` uniquely identifies a single event line. The `run_start` event is written when the stream is first opened (on the first `room_entered` or `combat_start` of a run); on resume it is skipped and the mod appends to the existing in-progress file.

Creature instance IDs (`target`, `dealer`, `applier`, `monster`, `monsters[].id`, `targets[]`) use `"{ModelId}:{CombatId}"` format (e.g. `"GREMLIN_NOB:2"`). `CombatId` is a `uint?` assigned sequentially by `CombatState.AttachCreature()`, unique per combat. See `TelemetryStreamWriter.CreatureId(Creature)`.

### Event Fields

| Event | Extra fields |
|-------|-------------|
| `run_start` | `game_version` (string — from `ReleaseInfoManager.Instance.ReleaseInfo?.Version`, falls back to `"dev"` for local builds), `profile` (int, 1–3 — `SaveManager.Instance.CurrentProfileId`), `run_id` (long) |
| `room_entered` | `room_type` (RoomType enum string), `floor` (int), `act` (int, 1-based), `player` (Steam64) |
| `combat_start` | `encounter`, `player` |
| `turn_start` | `encounter`, `turn`, `player`, `players` (array — each: `player` NetId, `character`, `hp`, `max_hp`, `block`, `energy`, `max_energy`, `powers[]` `{power, amount}`), `monsters` (array — each: `id` (creature instance ID), `hp`, `max_hp`, `block`, `powers[]` `{power, amount}`, `intents[]` `{type}`) |
| `card_draw` | `encounter`, `card`, `player`, `from_hand_draw` (bool — true = start-of-turn draw, false = effect-triggered), `turn`, `upgrade_level` (int — 0 = base, 1 = upgraded) |
| `card_play` | `encounter`, `card`, `player`, `target` (creature instance ID or null for untargeted), `turn`, `upgrade_level`, `is_auto_play` (bool — true = triggered by power/relic, false = played by player; **needs manual verification**) |
| `card_discard` | `encounter`, `card`, `player`, `from_flush` (bool — true = end-of-turn hand flush, false = explicit/effect discard), `turn`, `upgrade_level` |
| `card_exhaust` | `encounter`, `card`, `player`, `from_ethereal` (bool — true = Ethereal keyword auto-exhaust at end of turn, false = explicit card/power effect), `turn`, `upgrade_level` |
| `potion_use` | `encounter`, `potion`, `player`, `target` (creature instance ID, player's own for self-targeted, null for untargeted/AOE), `turn` |
| `power_applied` | `encounter`, `power` (PowerModel id), `target` (creature instance ID), `applier` (nullable creature instance ID — null when power self-decrements), `amount` (int, negative for stack reductions), `turn`, `player` |
| `damage_dealt` | `encounter`, `target` (creature instance ID), `dealer` (nullable creature instance ID), `hp_lost` (int), `blocked` (int), `overkill` (int), `turn`, `player` |
| `block_gained` | `encounter`, `target` (creature instance ID), `amount` (int), `turn`, `player` |
| `orb_channeled` | `encounter`, `player` (NetId ulong), `orb` (OrbModel id), `turn` — Defect only |
| `stars_gained` | `encounter`, `player` (NetId ulong), `amount` (int), `turn` — stars characters only |
| `relic_trigger` | `encounter`, `relic` (RelicModel id), `player` (owner NetId), `targets` (list of creature instance IDs the flash targeted), `turn` |
| `monster_action` | `encounter`, `monster` (creature instance ID), `move` (MoveState id), `intents` (list of IntentType strings), `targets` (list of creature instance IDs, empty if untargeted), `turn`, `player` |
| `rewards_offered` | `rewards` (array of `{reward_type, item}` — `reward_type`: `"card"` / `"relic"` / `"potion"` / `"gold"`; `item`: card/relic/potion ID or gold amount string), `floor`, `player` |
| `reward_taken` | `reward_type` (`"card"` / `"relic"` / `"potion"` / `"gold"`), `item` (ID string — null only for `gold` and skipped card rewards), `amount` (int — present only on `gold`), `floor`, `player` |
| `event_choice` | `event` (EventModel id), `option_key` (TextKey string of the chosen option), `floor`, `player` |
| `rest_site_choice` | `option` (OptionId string: `"HEAL"` / `"SMITH"` / `"DIG"` / `"CLONE"` / `"MEND"` / `"LIFT"` / `"HATCH"` / `"COOK"`), `floor`, `player` |
| `shop_offered` | `items` (array of `{item_type, item, cost}` — `item_type`: `"card"` / `"relic"` / `"potion"` / `"card_removal"`; `item`: ID string or null for `card_removal`; `cost`: int gold price), `floor`, `player` |
| `shop_purchase` | `item_type` (`"card"` / `"relic"` / `"potion"` / `"card_removal"`), `item` (ID string or null for `card_removal`), `gold_spent` (int), `floor`, `player` |
| `run_end` | `win`, `abandoned`, `character`, `ascension`, `num_players`, `player` |

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
- **Turn counter** is a `TurnCounter` class (mutable int wrapper) stored in a second `ConditionalWeakTable<CombatState, TurnCounter>`. Starts at 0, incremented to 1 on the first `BeforeSideTurnStart`. Cleaned up alongside the encounter ID table in `OnCombatEnd`.
- **`power_applied` patches `Hook.AfterPowerAmountChanged`**: fires for both gains (positive `amount`) and reductions (negative `amount`, e.g. Poison ticking down). Covers all creatures — player and monsters. `applier` is null when a power self-decrements. Creature IDs use `ModelId.Entry` (string) to be consistent with `potion_use` target.
- **`damage_dealt` patches `Hook.AfterDamageReceived`**: fires for every damage instance across all creatures. `combatState` is nullable — guarded. `hp_lost` / `blocked` / `overkill` come directly from `DamageResult` fields. Fires for both player-takes-damage and monster-takes-damage.
- **`block_gained` patches `Hook.AfterBlockGained`**: fires for all block gains including relic-granted values at combat start (e.g. 999 from Plating in AutoSlay).
- **`orb_channeled` patches `Hook.AfterOrbChanneled`**: Defect-specific; fires at `turn:0` for start-of-combat passive channels (e.g. GalvanicPower via `BeforeCombatStart`). Uses `player.NetId` to be consistent with existing player-identity fields on card events.
- **`stars_gained` patches `Hook.AfterStarsGained`**: character-specific (Watcher/stars characters). Will produce no events in a Defect run — this is expected.
- **`relic_trigger` patches `RelicModel.Flash(IEnumerable<Creature> targets)`**: the overload with targets is the one all activations route through (the no-arg `Flash()` delegates to it). `Flash` is called in non-combat contexts too (card reward screen, shop, treasure room) — guarded by `CombatManager.Instance.DebugOnlyGetState() == null`. `targets` are the creatures passed to `Flash`; no-arg `Flash()` passes just the owner's creature, so single-target relics emit a one-element list. `player` is `relic.Owner.NetId`.
- **Out-of-combat patches call `TelemetryStreamWriter` directly** (no `EncounterCardTracker` layer): out-of-combat events need no per-combat state (no `ConditionalWeakTable`), so the tracker is bypassed. `Open()` is called at the top of each out-of-combat patch — idempotent, so safe on every room entry.
- **`room_entered` patches `Hook.AfterRoomEntered`**: fires for all room types including combat rooms (redundant with `combat_start` but gives a clean floor-by-floor traversal log). `runState.TotalFloor` = floor number; `runState.CurrentActIndex + 1` = act. `room_type` is the `RoomType` enum name.
- **`rest_site_choice` uses `[HarmonyPatch]` + `TargetMethods()`** (not `[HarmonyPatch(typeof(RestSiteOption), ...)]`): `RestSiteOption.OnSelect()` is abstract — Harmony throws `"Abstract methods cannot be prepared"` if patched directly, which aborts `PatchAll()` and silently disables all subsequent patches. `TargetMethods()` discovers all non-abstract subclasses in the assembly at patch time, patching each concrete `OnSelect()` override instead. `OptionId` is public on each subclass (`"HEAL"`, `"SMITH"`, `"DIG"`, etc.). `Owner` is `protected` — accessed via a cached `PropertyInfo` reflection call.
- **`event_choice` patches `EventSynchronizer.ChooseOptionForEvent(Player, int)` (private method)**: referenced by name string in `[HarmonyPatch]`. `GetEventForPlayer(player)` is public and returns the active `EventModel`; `eventModel.CurrentOptions[optionIndex].TextKey` gives the same option key the game writes to its own history. Wrapped in try-catch since the private method patch could silently fail on game updates.
- **`shop_purchase` uses a two-patch pattern**: `MerchantEntry.ClearAfterPurchase()` runs before `Hook.AfterItemPurchased` fires, nulling item models. A Prefix on `MerchantEntry.OnTryPurchaseWrapper` captures item type + ID into a `ConditionalWeakTable<MerchantEntry, string>` before the async body runs. The `AfterItemPurchased` Postfix reads and removes from the table. `MerchantCardRemovalEntry` overrides `OnTryPurchaseWrapper` entirely (bypassing the base Prefix), so it is detected by type check in the Postfix with no item ID needed.
- **`shop_offered` is emitted inside `AfterRoomEnteredPatch`** (not a separate patch): `MerchantInventory.CreateForNormalMerchant` runs before `Hook.AfterRoomEntered` in `MerchantRoom.Enter`, so patching the factory directly caused `shop_offered` to appear before `room_entered` in the stream. Fix: enumerate `merchantRoom.GetLocalInventory()` inside `AfterRoomEnteredPatch` immediately after writing `room_entered`, guaranteeing correct NDJSON order from within the same method. (The Aug 2026 update made shops per-player — `MerchantRoom.Inventory` became `Inventories` (list) + `GetLocalInventory()`; `MerchantInventory.CardEntries` still exists as a convenience over the new `CharacterCardEntries` + `ColorlessCardEntries` split.) By `AfterRoomEntered` time, all inventory data is fully populated (cards via `MerchantCardEntry.Populate()`, relics/potions via constructors).
- **`rewards_offered` patches `Hook.ModifyRewards`** (was `Hook.BeforeRewardsOffered`, removed in the Aug 2026 update): `ModifyRewards(IRunState, Player, List<Reward> rewards, AbstractRoom?)` is a synchronous hook fired from `RewardsSet.GenerateWithoutOffering()` after the reward list is built and reward-modifiers have mutated it in place, so the Postfix sees the final list. **Caveat:** `GenerateWithoutOffering()` also runs on the non-offering paths (`RewardsCmd.GenerateForRoomEnd` / `GenerateCustom`), so this can in principle fire for rewards never shown; normal combat/treasure rewards go `OfferForRoomEnd → Offer → GenerateWithoutOffering`, firing once per screen. `CardReward.Cards` and `PotionReward.Potion` are public. `RelicReward._relic` is private — accessed via a cached `FieldInfo` reflection. Gold rewards use `GoldReward.Amount` (public). Each card in a `CardReward` becomes a separate entry in the `rewards` array. Combined with `reward_taken`, consumers can reconstruct "offered but not picked" for every reward screen.
- **`reward_taken` patches `Hook.AfterRewardTaken`**: `RelicReward.ClaimedRelic` and `PotionReward.ClaimedPotion` are set during `OnSelect()` and are accessible by the time the hook fires. For `CardReward`, `CardReward.Cards` is a live LINQ projection over `_cards`; `OnSelect()` calls `_cards.RemoveAll(c => c.Card == result)` for the chosen card before `AfterRewardTaken` fires. `RewardsOfferedPatch` snapshots the full card ID list into a `ConditionalWeakTable<CardReward, List<string>>`; `RewardTakenPatch` diffs the snapshot against the post-pick `Cards` to recover the taken card ID. If the player skips (no card removed), `item` is null. Gold rewards include `amount` instead of `item`.
- **`monster_action` patches `CombatHistory.MonsterPerformedMove`**: called at the end of `MonsterModel.PerformMove()` after the move resolves — same pattern as `CombatHistory.CardPlayStarted`. Fires during the enemy turn (between player `turn_end` N and `turn_start` N+1), so events carry `turn: N`. `move` is `MoveState.StateId` (the move name string, e.g. `"BASH"`). `intents` are the `IntentType` strings from the move's intent list — confirms what actually happened. `targets` is the list of creature `ModelId.Entry` values (`CombatHistory.MonsterPerformedMove` passes `targets` as the player creatures; null targets become an empty list).
- **`run_id` is `RunManager.Instance.ToSave(null).StartTime`**: a `long` Unix seconds value set at run creation and loaded from the save file on resume — stable across process restarts. Captured once in `Open()` and written explicitly on every event. `(player, run_id)` uniquely identifies a run globally. `run_id` is also used to name the temp file (`{run_id}.in_progress.expanded_run`) so that resume detection is exact: if the file exists at `Open()` time, we append and skip re-emitting `run_start`; if it doesn't, we create fresh. A crash followed by a different run leaves an orphaned `{old_run_id}.in_progress.expanded_run` which is harmless — new run gets its own file.
- **Multi-output routing via `_isOpen` + `_writeToFile` + `_sendToServer` flags**: all three are captured once in `Open()` from `ModEntry.Config` and held for the duration of the run. `WriteEvent()` serializes the event to JSON once, then writes to the file writer (if enabled) and/or enqueues to `RemoteSender` (if enabled). Config changes mid-run are not reflected until the next run.
- **Remote streaming is fire-and-forget**: `RemoteSender` drains a `ConcurrentQueue<string>` in a background `Task.Run` loop every 200ms, POSTing NDJSON batches to the configured URL. The queue is bounded at 2000 events; overflow is dropped with a log warning. Failed POSTs drop the batch and log. Game thread only calls `Enqueue()` (O(1), never blocks). `Finalize()` cancels the drain loop and calls `RemoteSender.Flush(3000)` — best-effort 3s final drain.
- **`TelemetryConfig` is a self-contained JSON file** (no BaseLib or in-game UI): written to `{OS.GetUserDataDir()}/mod_configs/expanded-telemetry.cfg` on first load with defaults; loaded once in `ModEntry.Init()`. `OS.GetUserDataDir()` is safe to call at init time (Godot is up by then). If `SendToServer=true` and `ServerUrl` is empty, an error is logged and remote output is disabled for that run.
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
- `relic_trigger` events can appear anywhere within a combat (turn:0 through final turn); volume varies by relic loadout (~2–5 per combat typical); `targets` is always the owner's creature for all observed relics; `player` matches the relic owner; `turn:0` is valid for relics that fire during `BeforeCombatStart` (e.g. BLOOD_PACT, ANCHOR)
- `room_entered` appears before every `combat_start` on combat floors; `room_type` is one of `Monster`, `Elite`, `Boss`, `Event`, `Shop`, `RestSite`, `Treasure`; `floor` increments monotonically across the run; `act` is 1, 2, or 3
- `rest_site_choice` `option` is one of: `HEAL`, `SMITH`, `DIG`, `CLONE`, `MEND`, `LIFT`, `HATCH`, `COOK`; appears after a `room_entered` with `room_type: "RestSite"`
- `event_choice` `event` is a non-empty model ID (e.g. `"NEOW"`, `"BIG_FISH"`); `option_key` is a non-empty dotted path string (e.g. `"NEOW.pages.INITIAL.options.ARCANE_SCROLL"`); appears after a `room_entered` with `room_type: "Event"`
- `shop_offered` appears once per shop floor, after `room_entered` with `room_type: "Shop"`; `items` array contains all cards, relics, potions, and exactly one `card_removal` entry; `cost` > 0 for all entries; `item_type` distribution per shop is typically ~5 cards + 1-2 relics + 1-2 potions + 1 card_removal; all card/relic/potion `item` fields are non-null; correlate with `shop_purchase` on `floor` + `player` to find what was available vs. bought
- `shop_purchase` `item_type` is one of `card`, `relic`, `potion`, `card_removal`; `item` is non-null for card/relic/potion, null for card_removal; `gold_spent` > 0; appears after a `room_entered` with `room_type: "Shop"`
- `rewards_offered` appears after each combat before rewards are claimed; one event per combat; `rewards` array contains gold entry + 3 card entries typically; each card on the reward screen is a separate entry
- `reward_taken` appears when player accepts a reward; `reward_type: "card"` has non-null `item` with the card ID (null only if the player somehow skips without picking); `reward_type: "gold"` has `amount` field and no `item`; `reward_type: "relic"` / `"potion"` have non-null `item`
- `monster_action` events appear between `turn_end N` and `turn_start N+1`; each living enemy in the encounter produces one per enemy turn; `intents` matches the move's intent type(s); `targets` is non-empty for all observed move types including buffs/sleep (the game always passes player creatures as targets to `MonsterPerformedMove` regardless of move type); multi-intent moves (e.g. `["Attack","Debuff"]`) are correctly serialized; empty `intents` is valid for moves like `DEAD_MOVE` on dead segments

## Sample Telemetry Files

`logs/1776012547.expanded_run` — Defect run (win, 23 combats). Contains both `from_flush: true` and `from_flush: false` discard events; the false events occur during TEST_SUBJECT_BOSS which forces a full hand discard at combat start.
