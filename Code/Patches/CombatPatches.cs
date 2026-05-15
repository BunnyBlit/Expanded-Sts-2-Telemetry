using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace ExpandedTelemetry;

[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatStart))]
public static class BeforeCombatStartPatch
{
    public static void Prefix(IRunState runState, CombatState? combatState)
    {
        Log.Info("[expanded-telemetry] BeforeCombatStartPatch");
        TelemetryStreamWriter.Open();
        if (combatState != null)
            EncounterCardTracker.OnCombatStart(combatState);
    }
}

[HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.CardPlayStarted))]
public static class CardPlayStartedPatch
{
    public static void Postfix(CombatState combatState, CardPlay cardPlay)
    {
        Log.Info($"[expanded-telemetry] After card play {cardPlay.Card.Id}");
        if (cardPlay.Card.Owner != null)
            EncounterCardTracker.OnCardPlayed(combatState, cardPlay.Card.Owner.NetId, cardPlay.Card.Owner.Character.Id.Entry, cardPlay.Card.Id.Entry, cardPlay.Target?.ModelId.Entry, cardPlay.Card.CurrentUpgradeLevel, cardPlay.IsAutoPlay);
    }
}

// Patches AfterCardChangedPiles rather than CombatHistory.CardDiscarded because
// end-of-turn flush discards go through CardPileCmd.Add directly (bypassing
// CombatHistory.CardDiscarded) while still firing AfterCardChangedPiles.
// Filtering to Hand→Discard transitions covers both explicit and flush discards.
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardChangedPiles))]
public static class CardChangedPilesPatch
{
    public static void Postfix(CombatState? combatState, CardModel card, PileType oldPile)
    {
        if (combatState == null) return;
        if (oldPile != PileType.Hand) return;
        if (card.Pile?.Type != PileType.Discard) return;
        if (card.Owner == null) return;
        bool fromFlush = CombatManager.Instance.EndingPlayerTurnPhaseTwo;
        Log.Info($"[expanded-telemetry] Card discarded (hand→discard, fromFlush={fromFlush}): {card.Id}");
        EncounterCardTracker.OnCardDiscarded(combatState, card.Owner.NetId, card.Owner.Character.Id.Entry, card.Id.Entry, fromFlush, card.CurrentUpgradeLevel);
    }
}

[HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.CardDrawn))]
public static class CardDrawnPatch
{
    public static void Postfix(CombatState combatState, CardModel card, bool fromHandDraw)
    {
        Log.Info($"[expanded-telemetry] After card draw {card.Id}");
        if (card.Owner != null)
            EncounterCardTracker.OnCardDrawn(combatState, card.Owner.NetId, card.Owner.Character.Id.Entry, card.Id.Entry, fromHandDraw, card.CurrentUpgradeLevel);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardExhausted))]
public static class CardExhaustedPatch
{
    public static void Postfix(CombatState combatState, CardModel card, bool causedByEthereal)
    {
        if (card.Owner == null) return;
        Log.Info($"[expanded-telemetry] Card exhausted (fromEthereal={causedByEthereal}): {card.Id}");
        EncounterCardTracker.OnCardExhausted(combatState, card.Owner.NetId, card.Owner.Character.Id.Entry, card.Id.Entry, causedByEthereal, card.CurrentUpgradeLevel);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPotionUsed))]
public static class PotionUsedPatch
{
    public static void Postfix(CombatState? combatState, PotionModel potion, Creature? target)
    {
        if (combatState == null) return;
        if (potion.Owner == null) return;
        Log.Info($"[expanded-telemetry] Potion used: {potion.Id}");
        EncounterCardTracker.OnPotionUsed(combatState, potion.Owner.NetId, potion.Owner.Character.Id.Entry, potion.Id.Entry, target?.ModelId.Entry);
    }
}

// BeforeSideTurnStart fires before draws, so cards drawn at turn start are correctly
// tagged with the new turn number. AfterPlayerTurnStart fires after draws — too late.
[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeSideTurnStart))]
public static class BeforeSideTurnStartPatch
{
    public static void Postfix(CombatState combatState, CombatSide side)
    {
        if (side != CombatSide.Player) return;
        Log.Info("[expanded-telemetry] Player turn started");
        EncounterCardTracker.OnTurnStart(combatState);
    }
}

// AfterTurnEnd fires for any CombatSide (player or enemy). Filter to player only.
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterTurnEnd))]
public static class AfterTurnEndPatch
{
    public static void Postfix(CombatState combatState, CombatSide side)
    {
        if (side != CombatSide.Player) return;
        Log.Info("[expanded-telemetry] Player turn ended");
        EncounterCardTracker.OnTurnEnd(combatState);
    }
}

// Hook.AfterCombatEnd only fires on the victory path (EndCombatInternal).
// Defeat is handled separately by LoseCombatPatch below.
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCombatEnd))]
public static class AfterCombatEndPatch
{
    public static void Prefix(IRunState runState, CombatState? combatState, CombatRoom room)
    {
        Log.Info("[expanded-telemetry] After combat ended (victory)");
        if (combatState != null)
            EncounterCardTracker.OnCombatEnd(combatState, "victory");
    }
}

// LoseCombat is called once when the defeat is first registered (guarded internally
// against duplicate calls). ProcessPendingLoss — which actually ends the combat — does
// not fire Hook.AfterCombatEnd, so we hook here for the defeat outcome.
[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.LoseCombat))]
public static class LoseCombatPatch
{
    public static void Prefix()
    {
        if (CombatManager.Instance.IsAboutToLose) return; // already registered, skip
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState == null) return;
        Log.Info("[expanded-telemetry] Combat lost (defeat)");
        EncounterCardTracker.OnCombatEnd(combatState, "defeat");
    }
}

[HarmonyPatch(typeof(RunHistoryUtilities), nameof(RunHistoryUtilities.CreateRunHistoryEntry))]
public static class CreateRunHistoryEntryPatch
{
    public static void Postfix(SerializableRun run, bool victory, bool isAbandoned)
    {
        ulong localPlayerId = PlatformUtil.GetLocalPlayerId(run.PlatformType);
        var localPlayer = run.Players.Find(p => p.NetId == localPlayerId) ?? run.Players[0];

        TelemetryStreamWriter.Finalize(
            startTime: run.StartTime,
            win: victory,
            abandoned: isAbandoned,
            character: localPlayer.CharacterId?.Entry ?? string.Empty,
            ascension: run.Ascension,
            numPlayers: run.Players.Count
        );
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPowerAmountChanged))]
public static class AfterPowerAmountChangedPatch
{
    public static void Postfix(CombatState combatState, PowerModel power, decimal amount, Creature? applier)
    {
        Log.Info($"[expanded-telemetry] Power amount changed: {power.Id.Entry} on {power.Owner.ModelId.Entry} by {(int)amount}");
        EncounterCardTracker.OnPowerAmountChanged(combatState, power.Id.Entry, power.Owner.ModelId.Entry, applier?.ModelId.Entry, (int)amount);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageReceived))]
public static class AfterDamageReceivedPatch
{
    public static void Postfix(CombatState? combatState, Creature target, DamageResult result, Creature? dealer)
    {
        if (combatState == null) return;
        EncounterCardTracker.OnDamageReceived(combatState, target.ModelId.Entry, dealer?.ModelId.Entry, result.UnblockedDamage, result.BlockedDamage, result.OverkillDamage);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterBlockGained))]
public static class AfterBlockGainedPatch
{
    public static void Postfix(CombatState combatState, Creature creature, decimal amount)
    {
        EncounterCardTracker.OnBlockGained(combatState, creature.ModelId.Entry, (int)amount);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterOrbChanneled))]
public static class AfterOrbChanneledPatch
{
    public static void Postfix(CombatState combatState, Player player, OrbModel orb)
    {
        EncounterCardTracker.OnOrbChanneled(combatState, player.NetId, player.Character.Id.Entry, orb.Id.Entry);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterStarsGained))]
public static class AfterStarsGainedPatch
{
    public static void Postfix(CombatState combatState, int amount, Player gainer)
    {
        EncounterCardTracker.OnStarsGained(combatState, gainer.NetId, gainer.Character.Id.Entry, amount);
    }
}

// RelicModel.Flash(IEnumerable<Creature>) is the single method all relic activations route
// through. The no-arg Flash() delegates to it. Guarded to combat-only via DebugOnlyGetState.
[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.Flash), new[] { typeof(IEnumerable<Creature>) })]
public static class RelicFlashPatch
{
    public static void Postfix(RelicModel __instance, IEnumerable<Creature> targets)
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState == null) return;
        var targetIds = targets.Select(t => t.ModelId.Entry).ToList();
        Log.Info($"[expanded-telemetry] Relic triggered: {__instance.Id.Entry}");
        EncounterCardTracker.OnRelicTriggered(combatState, __instance.Id.Entry, __instance.Owner.NetId, targetIds);
    }
}

// CombatHistory.MonsterPerformedMove is called at the end of MonsterModel.PerformMove()
// after the move resolves — same pattern as CombatHistory.CardPlayStarted for cards.
[HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.MonsterPerformedMove))]
public static class MonsterPerformedMovePatch
{
    public static void Postfix(CombatState combatState, MonsterModel monster, MoveState move, IEnumerable<Creature>? targets)
    {
        var intents = move.Intents.Select(i => i.IntentType.ToString()).ToList();
        var targetIds = targets?.Select(t => t.ModelId.Entry).ToList() ?? [];
        Log.Info($"[expanded-telemetry] Monster action: {monster.Id.Entry} performed {move.StateId}");
        EncounterCardTracker.OnMonsterAction(combatState, monster.Creature.ModelId.Entry, move.StateId, intents, targetIds);
    }
}
