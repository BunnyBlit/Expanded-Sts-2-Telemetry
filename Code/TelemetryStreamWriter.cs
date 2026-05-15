using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace ExpandedTelemetry;

// Used by RewardsOfferedPatch in OutOfCombatPatches.cs — must be internal, not private.
internal sealed record RewardSummary(string reward_type, string? item);

// Used by ShopOfferedPatch in OutOfCombatPatches.cs — must be internal, not private.
internal sealed record ShopItemSummary(string item_type, string? item, int cost);

// Streams telemetry events to disk as NDJSON (one JSON object per line) while a run
// is in progress. The file is written to `in_progress.encounter_cards` and renamed to
// `{StartTime}.encounter_cards` when the run ends, matching the game's .run file naming.
internal static class TelemetryStreamWriter
{
    private const string TempFileName = "in_progress.encounter_cards";

    private static StreamWriter? _writer;
    private static string? _tempFilePath;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // Snapshot types for turn_start state. Property names are snake_case so
    // System.Text.Json serializes them directly without a naming policy.
    private sealed record PowerEntry(string power, int amount);
    private sealed record IntentEntry(string type);
    private sealed record PlayerStateEntry(ulong player, string character, int hp, int max_hp, int block, int energy, int max_energy, List<PowerEntry> powers);
    private sealed record MonsterStateEntry(string id, int hp, int max_hp, int block, List<PowerEntry> powers, List<IntentEntry> intents);

    // Called once at the start of the first combat in a run. Idempotent — safe to call
    // on every BeforeCombatStart; opens the file only if not already open.
    public static void Open()
    {
        if (_writer != null) return;
        try
        {
            _tempFilePath = GetHistoryFilePath(TempFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(_tempFilePath)!);
            _writer = new StreamWriter(_tempFilePath, append: false) { AutoFlush = true };
            WriteEvent(new { event_type = "run_start", timestamp = Now });
        }
        catch (Exception ex)
        {
            Log.Error("[expanded-telemetry] Failed to open telemetry stream: " + ex.Message);
        }
    }

    public static void WriteCombatStart(string encounterId)
        => WriteEvent(new { event_type = "combat_start", encounter = encounterId, timestamp = Now });

    public static void WriteTurnStart(string encounterId, int turn, IReadOnlyList<Player> players, IReadOnlyList<Creature> enemies)
    {
        var playerStates = players.Select(p => new PlayerStateEntry(
            player: p.NetId,
            character: p.Character.Id.Entry,
            hp: p.Creature.CurrentHp,
            max_hp: p.Creature.MaxHp,
            block: p.Creature.Block,
            energy: p.PlayerCombatState?.Energy ?? 0,
            max_energy: p.PlayerCombatState?.MaxEnergy ?? p.MaxEnergy,
            powers: p.Creature.Powers.Select(pw => new PowerEntry(pw.Id.Entry, pw.Amount)).ToList()
        )).ToList();
        var monsterStates = enemies
            .Where(e => e.IsAlive && e.IsMonster)
            .Select(e => new MonsterStateEntry(
                id: e.ModelId.Entry,
                hp: e.CurrentHp,
                max_hp: e.MaxHp,
                block: e.Block,
                powers: e.Powers.Select(pw => new PowerEntry(pw.Id.Entry, pw.Amount)).ToList(),
                intents: e.Monster!.NextMove.Intents.Select(i => new IntentEntry(i.IntentType.ToString())).ToList()
            )).ToList();
        WriteEvent(new { event_type = "turn_start", encounter = encounterId, turn, players = playerStates, monsters = monsterStates, timestamp = Now });
    }

    public static void WriteTurnEnd(string encounterId, int turn)
        => WriteEvent(new { event_type = "turn_end", encounter = encounterId, turn, timestamp = Now });

    public static void WriteCardPlay(string encounterId, ulong playerId, string characterId, string cardId, string? targetId, int turn, int upgradeLevel, bool isAutoPlay)
        => WriteEvent(new { event_type = "card_play", encounter = encounterId, card = cardId, player = playerId, character = characterId, target = targetId, turn, upgrade_level = upgradeLevel, is_auto_play = isAutoPlay, timestamp = Now });

    public static void WriteCardDraw(string encounterId, ulong playerId, string characterId, string cardId, bool fromHandDraw, int turn, int upgradeLevel)
        => WriteEvent(new { event_type = "card_draw", encounter = encounterId, card = cardId, player = playerId, character = characterId, from_hand_draw = fromHandDraw, turn, upgrade_level = upgradeLevel, timestamp = Now });

    public static void WriteCardDiscard(string encounterId, ulong playerId, string characterId, string cardId, bool fromFlush, int turn, int upgradeLevel)
        => WriteEvent(new { event_type = "card_discard", encounter = encounterId, card = cardId, player = playerId, character = characterId, from_flush = fromFlush, turn, upgrade_level = upgradeLevel, timestamp = Now });

    public static void WritePotionUse(string encounterId, ulong playerId, string characterId, string potionId, string? targetId, int turn)
        => WriteEvent(new { event_type = "potion_use", encounter = encounterId, potion = potionId, player = playerId, character = characterId, target = targetId, turn, timestamp = Now });

    public static void WriteCardExhaust(string encounterId, ulong playerId, string characterId, string cardId, bool fromEthereal, int turn, int upgradeLevel)
        => WriteEvent(new { event_type = "card_exhaust", encounter = encounterId, card = cardId, player = playerId, character = characterId, from_ethereal = fromEthereal, turn, upgrade_level = upgradeLevel, timestamp = Now });

    public static void WritePowerApplied(string encounterId, string powerId, string targetId, string? applierId, int amount, int turn)
        => WriteEvent(new { event_type = "power_applied", encounter = encounterId, power = powerId, target = targetId, applier = applierId, amount, turn, timestamp = Now });

    public static void WriteDamageDealt(string encounterId, string targetId, string? dealerId, int hpLost, int blocked, int overkill, int turn)
        => WriteEvent(new { event_type = "damage_dealt", encounter = encounterId, target = targetId, dealer = dealerId, hp_lost = hpLost, blocked, overkill, turn, timestamp = Now });

    public static void WriteBlockGained(string encounterId, string targetId, int amount, int turn)
        => WriteEvent(new { event_type = "block_gained", encounter = encounterId, target = targetId, amount, turn, timestamp = Now });

    public static void WriteOrbChanneled(string encounterId, ulong playerId, string characterId, string orbId, int turn)
        => WriteEvent(new { event_type = "orb_channeled", encounter = encounterId, player = playerId, character = characterId, orb = orbId, turn, timestamp = Now });

    public static void WriteStarsGained(string encounterId, ulong playerId, string characterId, int amount, int turn)
        => WriteEvent(new { event_type = "stars_gained", encounter = encounterId, player = playerId, character = characterId, amount, turn, timestamp = Now });

    public static void WriteMonsterAction(string encounterId, string monsterId, string moveId, List<string> intents, List<string> targets, int turn)
        => WriteEvent(new { event_type = "monster_action", encounter = encounterId, monster = monsterId, move = moveId, intents, targets, turn, timestamp = Now });

    public static void WriteRelicTriggered(string encounterId, string relicId, ulong playerId, List<string> targets, int turn)
        => WriteEvent(new { event_type = "relic_trigger", encounter = encounterId, relic = relicId, player = playerId, targets, turn, timestamp = Now });

    public static void WriteRoomEntered(string roomType, int floor, int act, ulong player)
        => WriteEvent(new { event_type = "room_entered", room_type = roomType, floor, act, player, timestamp = Now });

    public static void WriteRestSiteChoice(string optionId, int floor, ulong player)
        => WriteEvent(new { event_type = "rest_site_choice", option = optionId, floor, player, timestamp = Now });

    public static void WriteEventChoice(string eventId, string optionKey, int floor, ulong player)
        => WriteEvent(new { event_type = "event_choice", @event = eventId, option_key = optionKey, floor, player, timestamp = Now });

    public static void WriteShopPurchase(string itemType, string? itemId, int goldSpent, int floor, ulong player)
        => WriteEvent(new { event_type = "shop_purchase", item_type = itemType, item = itemId, gold_spent = goldSpent, floor, player, timestamp = Now });

    public static void WriteShopOffered(List<ShopItemSummary> items, int floor, ulong player)
        => WriteEvent(new { event_type = "shop_offered", items, floor, player, timestamp = Now });

    public static void WriteRewardsOffered(List<RewardSummary> rewards, int floor, ulong player)
        => WriteEvent(new { event_type = "rewards_offered", rewards, floor, player, timestamp = Now });

    public static void WriteRewardTaken(string rewardType, string? itemId, int? amount, int floor, ulong player)
    {
        if (amount.HasValue)
            WriteEvent(new { event_type = "reward_taken", reward_type = rewardType, item = itemId, amount = amount.Value, floor, player, timestamp = Now });
        else
            WriteEvent(new { event_type = "reward_taken", reward_type = rewardType, item = itemId, floor, player, timestamp = Now });
    }

    public static void WriteCombatEnd(string encounterId, string outcome)
        => WriteEvent(new { event_type = "combat_end", encounter = encounterId, outcome, timestamp = Now });

    // Called by CreateRunHistoryEntryPatch. Writes the final run_end event, closes the
    // stream, and renames the temp file to match the game's {StartTime}.run filename.
    public static void Finalize(long startTime, bool win, bool abandoned, string character, int ascension, int numPlayers)
    {
        try
        {
            WriteEvent(new { event_type = "run_end", win, abandoned, character, ascension, num_players = numPlayers, timestamp = Now });
            _writer?.Close();
            _writer = null;

            if (_tempFilePath != null && File.Exists(_tempFilePath))
            {
                string finalPath = GetHistoryFilePath($"{startTime}.encounter_cards");
                File.Move(_tempFilePath, finalPath, overwrite: true);
                Log.Info($"[expanded-telemetry] Finalized telemetry to {finalPath}");
                _tempFilePath = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error("[expanded-telemetry] Failed to finalize telemetry stream: " + ex.Message + "\n" + ex.StackTrace);
            _writer = null;
            _tempFilePath = null;
        }
    }

    private static void WriteEvent(object evt)
    {
        if (_writer == null) return;
        try
        {
            _writer.WriteLine(JsonSerializer.Serialize(evt, evt.GetType()));
        }
        catch (Exception ex)
        {
            Log.Error("[expanded-telemetry] Failed to write telemetry event: " + ex.Message);
        }
    }

    private static string GetHistoryFilePath(string fileName)
    {
        int profileId = SaveManager.Instance.CurrentProfileId;
        string accountBasePath = ProjectSettings.GlobalizePath(UserDataPathProvider.GetAccountScopedBasePath(""));
        string historyDir = Path.Combine(accountBasePath, RunHistorySaveManager.GetHistoryPath(profileId));
        return Path.Combine(historyDir, fileName);
    }
}
