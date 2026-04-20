using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;

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
        TelemetryStreamWriter.WriteTurnStart(encounterId, counter.Value);
    }

    public static void OnTurnEnd(CombatState combatState)
    {
        if (!_encounterIds.TryGetValue(combatState, out string? encounterId)) return;
        if (!_turnCounters.TryGetValue(combatState, out TurnCounter? counter)) return;
        TelemetryStreamWriter.WriteTurnEnd(encounterId, counter.Value);
    }

    private static int GetTurn(CombatState combatState)
        => _turnCounters.TryGetValue(combatState, out TurnCounter? counter) ? counter.Value : 0;

    public static void OnCardPlayed(CombatState combatState, ulong playerId, string cardId, string? targetId, int upgradeLevel, bool isAutoPlay)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteCardPlay(encounterId, playerId, cardId, targetId, GetTurn(combatState), upgradeLevel, isAutoPlay);
    }

    public static void OnCardDrawn(CombatState combatState, ulong playerId, string cardId, bool fromHandDraw, int upgradeLevel)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteCardDraw(encounterId, playerId, cardId, fromHandDraw, GetTurn(combatState), upgradeLevel);
    }

    public static void OnCardDiscarded(CombatState combatState, ulong playerId, string cardId, bool fromFlush, int upgradeLevel)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteCardDiscard(encounterId, playerId, cardId, fromFlush, GetTurn(combatState), upgradeLevel);
    }

    public static void OnPotionUsed(CombatState combatState, ulong playerId, string potionId, string? targetId)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WritePotionUse(encounterId, playerId, potionId, targetId, GetTurn(combatState));
    }

    public static void OnCardExhausted(CombatState combatState, ulong playerId, string cardId, bool fromEthereal, int upgradeLevel)
    {
        if (_encounterIds.TryGetValue(combatState, out string? encounterId))
            TelemetryStreamWriter.WriteCardExhaust(encounterId, playerId, cardId, fromEthereal, GetTurn(combatState), upgradeLevel);
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
