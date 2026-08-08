using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace ExpandedTelemetry;

// Mutable wrapper so we can store a turn counter in a ConditionalWeakTable.
internal sealed class TurnCounter
{
    public int Value;
}

// Maps each active ICombatState to its encounter ID and current turn number so
// card events can be tagged with encounter and turn without accumulating state.
internal static class EncounterCardTracker
{
    private static readonly ConditionalWeakTable<ICombatState, string> _encounterIds = new();
    private static readonly ConditionalWeakTable<ICombatState, TurnCounter> _turnCounters = new();
    // Marks combats whose combat_end has been written, so a stray second call can't
    // double-emit now that the encounter mapping is no longer removed on combat end.
    private static readonly ConditionalWeakTable<ICombatState, object> _ended = new();
    private static readonly object Sentinel = new();

    public static void OnCombatStart(ICombatState combatState)
    {
        if (combatState.Encounter == null) return;
        string encounterId = combatState.Encounter.Id.Entry;
        _encounterIds.Add(combatState, encounterId);
        _turnCounters.Add(combatState, new TurnCounter());
        TelemetryStreamWriter.WriteCombatStart(encounterId);
    }

    public static void OnTurnStart(ICombatState combatState)
    {
        if (!_encounterIds.TryGetValue(combatState, out string? encounterId)) return;
        if (!_turnCounters.TryGetValue(combatState, out TurnCounter? counter)) return;
        counter.Value++;
        TelemetryStreamWriter.WriteTurnStart(encounterId, counter.Value, combatState.Players, combatState.Enemies);
    }

    public static void OnTurnEnd(ICombatState combatState)
    {
        if (!_encounterIds.TryGetValue(combatState, out string? encounterId)) return;
        if (!_turnCounters.TryGetValue(combatState, out TurnCounter? counter)) return;
        TelemetryStreamWriter.WriteTurnEnd(encounterId, counter.Value);
    }

    private static int GetTurn(ICombatState combatState)
        => _turnCounters.TryGetValue(combatState, out TurnCounter? counter) ? counter.Value : 0;

    public static void OnCardPlayed(ICombatState combatState, ulong playerId, string characterId, string cardId, string? targetId, int upgradeLevel, bool isAutoPlay)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteCardPlay(encounterId, playerId, characterId, cardId, targetId, GetTurn(combatState), upgradeLevel, isAutoPlay);
    }

    public static void OnCardDrawn(ICombatState combatState, ulong playerId, string characterId, string cardId, bool fromHandDraw, int upgradeLevel)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteCardDraw(encounterId, playerId, characterId, cardId, fromHandDraw, GetTurn(combatState), upgradeLevel);
    }

    public static void OnCardDiscarded(ICombatState combatState, ulong playerId, string characterId, string cardId, bool fromFlush, int upgradeLevel)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteCardDiscard(encounterId, playerId, characterId, cardId, fromFlush, GetTurn(combatState), upgradeLevel);
    }

    public static void OnPotionUsed(ICombatState combatState, ulong playerId, string characterId, string potionId, string? targetId)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WritePotionUse(encounterId, playerId, characterId, potionId, targetId, GetTurn(combatState));
    }

    public static void OnCardExhausted(ICombatState combatState, ulong playerId, string characterId, string cardId, bool fromEthereal, int upgradeLevel)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteCardExhaust(encounterId, playerId, characterId, cardId, fromEthereal, GetTurn(combatState), upgradeLevel);
    }

    public static void OnPowerAmountChanged(ICombatState combatState, string powerId, string targetId, string? applierId, int amount)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WritePowerApplied(encounterId, powerId, targetId, applierId, amount, GetTurn(combatState));
    }

    public static void OnDamageReceived(ICombatState combatState, string targetId, string? dealerId, int hpLost, int blocked, int overkill)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteDamageDealt(encounterId, targetId, dealerId, hpLost, blocked, overkill, GetTurn(combatState));
    }

    public static void OnBlockGained(ICombatState combatState, string targetId, int amount)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteBlockGained(encounterId, targetId, amount, GetTurn(combatState));
    }

    public static void OnOrbChanneled(ICombatState combatState, ulong playerId, string characterId, string orbId)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteOrbChanneled(encounterId, playerId, characterId, orbId, GetTurn(combatState));
    }

    public static void OnStarsGained(ICombatState combatState, ulong playerId, string characterId, int amount)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteStarsGained(encounterId, playerId, characterId, amount, GetTurn(combatState));
    }

    public static void OnMonsterAction(ICombatState combatState, string monsterId, string moveId, List<string> intents, List<string> targets)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteMonsterAction(encounterId, monsterId, moveId, intents, targets, GetTurn(combatState));
    }

    public static void OnRelicTriggered(ICombatState combatState, string relicId, ulong playerId, List<string> targets)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteRelicTriggered(encounterId, relicId, playerId, targets, GetTurn(combatState));
    }

    public static void OnCombatEnd(ICombatState combatState, string outcome)
    {
        if (!_encounterIds.TryGetValue(combatState, out string? encounterId)) return;
        // Write combat_end at most once per combat. This guard replaces the old
        // "remove the mapping" approach, which dropped post-victory relic triggers:
        // relics like Burning Blood flash in Hook.AfterCombatVictory, which fires
        // AFTER Hook.AfterCombatEnd (where this runs), so OnRelicTriggered still needs
        // the encounter/turn mapping to resolve. We therefore keep the mappings and
        // let the ConditionalWeakTable GC-evict them once the CombatState is collected
        // (no leak — that is exactly why a CWT is used).
        if (_ended.TryGetValue(combatState, out _)) return;
        _ended.Add(combatState, Sentinel);
        TelemetryStreamWriter.WriteCombatEnd(encounterId, outcome);
    }
}
