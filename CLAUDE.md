# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Deploy

```bash
# Build and auto-install DLL to game mods folder
./deploy.sh                  # runs: dotnet build -c Debug

# Copy game log locally for inspection
./fetch-log.sh
```

The `.csproj` has a `CopyToModsFolderOnBuild` MSBuild target that automatically copies the compiled DLL and supporting files to:
`SlayTheSpire2.app/Contents/MacOS/mods/expanded-telemetry/`

Hot-reload is available via MCP: use `watch_project` with `sts2-mcp-watch.json` config (1.5s debounce, auto-reload on file save).

## Architecture

**expanded-telemetry** is a Slay the Spire 2 mod that streams gameplay telemetry as NDJSON to disk in real-time. It is purely observational (`affects_gameplay: false`).

### Core Modules (`Code/`)

| File | Purpose |
|------|---------|
| `ModEntry.cs` | `[ModInitializer]` entry point — initializes Harmony and calls `PatchAll()` |
| `TelemetryStreamWriter.cs` | All file I/O — opens/writes/finalizes the NDJSON stream |
| `EncounterCardTracker.cs` | Maps `CombatState → encounterID` via `ConditionalWeakTable`; dispatches events to the writer |
| `Patches/CombatPatches.cs` | 10 Harmony patches that hook game events and call into `EncounterCardTracker` |

### Event Flow

1. `BeforeCombatStartPatch` → opens stream (idempotent) + registers combat
2. `BeforeSideTurnStartPatch` (player side only) → write `turn_start`, increment turn counter
3. `CardDrawnPatch` / `CardPlayStartedPatch` / `CardChangedPilesPatch` / `CardExhaustedPatch` / `PotionUsedPatch` → lookup encounter ID + turn number → write event
4. `AfterTurnEndPatch` (player side only) → write `turn_end`
5. `AfterCombatEndPatch` / `LoseCombatPatch` → write `combat_end`, remove combat from table
6. `CreateRunHistoryEntryPatch` → write `run_end`, close stream, rename file

### Output Files

- **In-progress**: `in_progress.encounter_cards` (AutoFlush=true, crash-safe)
- **Finalized**: `{StartTime}.encounter_cards` (renamed on run end)
- **Location**: `~/Library/Application Support/SlayTheSpire2/steam/{UserID}/modded/profile{N}/saves/history/`

### Event Types (NDJSON)

`run_start`, `combat_start`, `turn_start`, `card_draw`, `card_play`, `card_discard`, `card_exhaust`, `potion_use`, `turn_end`, `combat_end`, `run_end`

Each event includes a `timestamp` (Unix seconds UTC). The `run_start` event is written on the first `combat_start` of a run (detected by checking if the stream is already open).

### Event Fields

| Event | Extra fields |
|-------|-------------|
| `combat_start` | `encounter` |
| `turn_start` | `encounter`, `turn` |
| `card_draw` | `encounter`, `card`, `player`, `from_hand_draw` (bool — true = start-of-turn draw, false = effect-triggered), `turn`, `upgrade_level` (int — 0 = base, 1 = upgraded) |
| `card_play` | `encounter`, `card`, `player`, `target` (monster ID or null for untargeted), `turn`, `upgrade_level`, `is_auto_play` (bool — true = triggered by power/relic, false = played by player; **needs manual verification**) |
| `card_discard` | `encounter`, `card`, `player`, `from_flush` (bool — true = end-of-turn hand flush, false = explicit/effect discard), `turn`, `upgrade_level` |
| `card_exhaust` | `encounter`, `card`, `player`, `from_ethereal` (bool — true = Ethereal keyword auto-exhaust at end of turn, false = explicit card/power effect), `turn`, `upgrade_level` |
| `potion_use` | `encounter`, `potion`, `player`, `target` (creature ID, player's own ID for self-targeted, null for untargeted/AOE), `turn` |
| `turn_end` | `encounter`, `turn` |
| `combat_end` | `encounter`, `outcome` (`"victory"` or `"defeat"`) |
| `run_end` | `win`, `abandoned`, `character`, `ascension`, `num_players` |

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
- **`turn_start` patches `Hook.BeforeSideTurnStart`** (not `AfterPlayerTurnStart`): `AfterPlayerTurnStart` fires *after* `CardPileCmd.Draw` in `SetupPlayerTurn`, so start-of-turn draws would be tagged with the previous turn number. `BeforeSideTurnStart` fires before draws. Filter: `side == CombatSide.Player`.
- **`turn_end` patches `Hook.AfterTurnEnd`** filtered to `CombatSide.Player`: fires after the end-of-turn flush in `EndPlayerTurnPhaseTwoInternal`, so flush discard events are bracketed inside the correct turn.
- **Turn counter** is a `TurnCounter` class (mutable int wrapper) stored in a second `ConditionalWeakTable<CombatState, TurnCounter>`. Starts at 0, incremented to 1 on the first `BeforeSideTurnStart`. Cleaned up alongside the encounter ID table in `OnCombatEnd`.

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
- All `card_draw`, `card_play`, `card_discard`, `card_exhaust`, `potion_use` events have `turn >= 1`; card events within a `turn_start N` / `turn_end N` window all carry `turn: N`
- `turn_start` appears before any card draws for that turn (including start-of-turn hand draws)

## Sample Telemetry Files

`logs/1776012547.encounter_cards` — Defect run (win, 23 combats). Contains both `from_flush: true` and `from_flush: false` discard events; the false events occur during TEST_SUBJECT_BOSS which forces a full hand discard at combat start.
