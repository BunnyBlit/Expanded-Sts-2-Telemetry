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

// Maps each active CombatState to its encounter ID and current turn number so
// card events can be tagged with encounter and turn without accumulating state.
internal static class EncounterCardTracker
{
    private static readonly ConditionalWeakTable<CombatState, string> _encounterIds = new();
    private static readonly ConditionalWeakTable<CombatState, TurnCounter> _turnCounters = new();

    public static void OnCombatStart(CombatState combatState)
    {
        if (combatState.Encounter == null) return;
        string encounterId = combatState.Encounter.Id.Entry;
        _encounterIds.Add(combatState, encounterId);
        _turnCounters.Add(combatState, new TurnCounter());
        TelemetryStreamWriter.WriteCombatStart(encounterId);
    }

    public static void OnTurnStart(CombatState combatState)
    {
        if (!_encounterIds.TryGetValue(combatState, out string? encounterId)) return;
        if (!_turnCounters.TryGetValue(combatState, out TurnCounter? counter)) return;
        counter.Value++;
        TelemetryStreamWriter.WriteTurnStart(encounterId, counter.Value, combatState.Players, combatState.Enemies);
    }

    public static void OnTurnEnd(CombatState combatState)
    {
        if (!_encounterIds.TryGetValue(combatState, out string? encounterId)) return;
        if (!_turnCounters.TryGetValue(combatState, out TurnCounter? counter)) return;
        TelemetryStreamWriter.WriteTurnEnd(encounterId, counter.Value);
    }

    private static int GetTurn(CombatState combatState)
        => _turnCounters.TryGetValue(combatState, out TurnCounter? counter) ? counter.Value : 0;

    public static void OnCardPlayed(CombatState combatState, ulong playerId, string characterId, string cardId, string? targetId, int upgradeLevel, bool isAutoPlay)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteCardPlay(encounterId, playerId, characterId, cardId, targetId, GetTurn(combatState), upgradeLevel, isAutoPlay);
    }

    public static void OnCardDrawn(CombatState combatState, ulong playerId, string characterId, string cardId, bool fromHandDraw, int upgradeLevel)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteCardDraw(encounterId, playerId, characterId, cardId, fromHandDraw, GetTurn(combatState), upgradeLevel);
    }

    public static void OnCardDiscarded(CombatState combatState, ulong playerId, string characterId, string cardId, bool fromFlush, int upgradeLevel)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteCardDiscard(encounterId, playerId, characterId, cardId, fromFlush, GetTurn(combatState), upgradeLevel);
    }

    public static void OnPotionUsed(CombatState combatState, ulong playerId, string characterId, string potionId, string? targetId)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WritePotionUse(encounterId, playerId, characterId, potionId, targetId, GetTurn(combatState));
    }

    public static void OnCardExhausted(CombatState combatState, ulong playerId, string characterId, string cardId, bool fromEthereal, int upgradeLevel)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteCardExhaust(encounterId, playerId, characterId, cardId, fromEthereal, GetTurn(combatState), upgradeLevel);
    }

    public static void OnPowerAmountChanged(CombatState combatState, string powerId, string targetId, string? applierId, int amount)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WritePowerApplied(encounterId, powerId, targetId, applierId, amount, GetTurn(combatState));
    }

    public static void OnDamageReceived(CombatState combatState, string targetId, string? dealerId, int hpLost, int blocked, int overkill)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteDamageDealt(encounterId, targetId, dealerId, hpLost, blocked, overkill, GetTurn(combatState));
    }

    public static void OnBlockGained(CombatState combatState, string targetId, int amount)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteBlockGained(encounterId, targetId, amount, GetTurn(combatState));
    }

    public static void OnOrbChanneled(CombatState combatState, ulong playerId, string characterId, string orbId)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteOrbChanneled(encounterId, playerId, characterId, orbId, GetTurn(combatState));
    }

    public static void OnStarsGained(CombatState combatState, ulong playerId, string characterId, int amount)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteStarsGained(encounterId, playerId, characterId, amount, GetTurn(combatState));
    }

    public static void OnMonsterAction(CombatState combatState, string monsterId, string moveId, List<string> intents, List<string> targets)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteMonsterAction(encounterId, monsterId, moveId, intents, targets, GetTurn(combatState));
    }

    public static void OnRelicTriggered(CombatState combatState, string relicId, ulong playerId, List<string> targets)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteRelicTriggered(encounterId, relicId, playerId, targets, GetTurn(combatState));
    }

    public static void OnCombatEnd(CombatState combatState, string outcome)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
        {
            TelemetryStreamWriter.WriteCombatEnd(encounterId, outcome);
            _encounterIds.Remove(combatState);
            _turnCounters.Remove(combatState);
        }
    }
}
