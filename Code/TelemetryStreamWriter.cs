using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace ExpandedTelemetry;

// Used by RewardsOfferedPatch in OutOfCombatPatches.cs — must be internal, not private.
internal sealed record RewardSummary(string reward_type, string? item);

// Used by ShopOfferedPatch in OutOfCombatPatches.cs — must be internal, not private.
internal sealed record ShopItemSummary(string item_type, string? item, int cost);

// Routes telemetry events to one or both outputs (local file, remote server) based on
// TelemetryConfig. Outputs are configured once at Open() time and held for the run.
internal static class TelemetryStreamWriter
{
    private const string FileExtension = "expanded_run";

    private static bool _isOpen;
    private static bool _writeToFile;
    private static bool _sendToServer;
    private static ulong _localPlayerId;
    private static long _runId;
    private static StreamWriter? _writer;
    private static string? _tempFilePath;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // Returns "{ModelId}:{CombatId}" — unique per creature instance within a combat.
    // Falls back to ModelId alone if CombatId is unassigned (should not happen in practice).
    internal static string CreatureId(Creature creature)
        => creature.CombatId.HasValue
            ? $"{creature.ModelId.Entry}:{creature.CombatId.Value}"
            : creature.ModelId.Entry;

    // Snapshot types for turn_start state. Property names are snake_case so
    // System.Text.Json serializes them directly without a naming policy.
    private sealed record PowerEntry(string power, int amount);
    private sealed record IntentEntry(string type);
    private sealed record PlayerStateEntry(ulong player, string character, int hp, int max_hp, int block, int energy, int max_energy, List<PowerEntry> powers);
    private sealed record MonsterStateEntry(string id, int hp, int max_hp, int block, List<PowerEntry> powers, List<IntentEntry> intents);

    // Idempotent — safe to call from every room entry and combat start.
    // Reads config once per run; outputs are fixed until Finalize() resets state.
    // On resume (process restart mid-run), RunManager preserves the original StartTime,
    // so _runId is stable and we append to the existing in-progress file instead of overwriting.
    public static void Open()
    {
        if (_isOpen) return;
        _isOpen = true;
        try
        {
            var config = ModEntry.Config;
            _localPlayerId = PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform);
            _writeToFile = config.WriteToFile;
            _sendToServer = config.SendToServer;
            _runId = RunManager.Instance.ToSave(null).StartTime;

            if (_sendToServer)
            {
                if (string.IsNullOrWhiteSpace(config.ServerUrl))
                {
                    Log.Error("[expanded-telemetry] SendToServer is enabled but ServerUrl is not set. " +
                              "Edit mod_configs/expanded-telemetry.cfg to add a ServerUrl. Remote streaming disabled for this run.");
                    _sendToServer = false;
                }
                else
                {
                    RemoteSender.Start(config.ServerUrl);
                }
            }

            if (!_writeToFile && !_sendToServer)
                Log.Warn("[expanded-telemetry] Both WriteToFile and SendToServer are disabled — no telemetry will be recorded.");

            bool resuming = false;
            if (_writeToFile)
            {
                _tempFilePath = GetHistoryFilePath($"{_runId}.in_progress.{FileExtension}");
                Directory.CreateDirectory(Path.GetDirectoryName(_tempFilePath)!);
                resuming = File.Exists(_tempFilePath);
                _writer = new StreamWriter(_tempFilePath, append: resuming) { AutoFlush = true };
                if (resuming)
                    Log.Info($"[expanded-telemetry] Resuming run {_runId}, appending to existing file.");
            }

            if (!resuming)
            {
                string gameVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "dev";
                int profileId = SaveManager.Instance.CurrentProfileId;
                WriteEvent(new { event_type = "run_start", player = _localPlayerId, game_version = gameVersion, profile = profileId, run_id = _runId, timestamp = Now });
            }
        }
        catch (Exception ex)
        {
            Log.Error("[expanded-telemetry] Failed to open telemetry stream: " + ex.Message);
            _isOpen = false;
            _writeToFile = false;
            _sendToServer = false;
            _localPlayerId = 0;
            _runId = 0;
            _writer = null;
            _tempFilePath = null;
        }
    }

    public static void WriteCombatStart(string encounterId)
        => WriteEvent(new { event_type = "combat_start", encounter = encounterId, player = _localPlayerId, run_id = _runId, timestamp = Now });

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
                id: CreatureId(e),
                hp: e.CurrentHp,
                max_hp: e.MaxHp,
                block: e.Block,
                powers: e.Powers.Select(pw => new PowerEntry(pw.Id.Entry, pw.Amount)).ToList(),
                intents: e.Monster!.NextMove.Intents.Select(i => new IntentEntry(i.IntentType.ToString())).ToList()
            )).ToList();
        WriteEvent(new { event_type = "turn_start", encounter = encounterId, turn, player = _localPlayerId, players = playerStates, monsters = monsterStates, run_id = _runId, timestamp = Now });
    }

    public static void WriteTurnEnd(string encounterId, int turn)
        => WriteEvent(new { event_type = "turn_end", encounter = encounterId, turn, player = _localPlayerId, run_id = _runId, timestamp = Now });

    public static void WriteCardPlay(string encounterId, ulong playerId, string characterId, string cardId, string? targetId, int turn, int upgradeLevel, bool isAutoPlay)
        => WriteEvent(new { event_type = "card_play", encounter = encounterId, card = cardId, player = playerId, character = characterId, target = targetId, turn, upgrade_level = upgradeLevel, is_auto_play = isAutoPlay, run_id = _runId, timestamp = Now });

    public static void WriteCardDraw(string encounterId, ulong playerId, string characterId, string cardId, bool fromHandDraw, int turn, int upgradeLevel)
        => WriteEvent(new { event_type = "card_draw", encounter = encounterId, card = cardId, player = playerId, character = characterId, from_hand_draw = fromHandDraw, turn, upgrade_level = upgradeLevel, run_id = _runId, timestamp = Now });

    public static void WriteCardDiscard(string encounterId, ulong playerId, string characterId, string cardId, bool fromFlush, int turn, int upgradeLevel)
        => WriteEvent(new { event_type = "card_discard", encounter = encounterId, card = cardId, player = playerId, character = characterId, from_flush = fromFlush, turn, upgrade_level = upgradeLevel, run_id = _runId, timestamp = Now });

    public static void WritePotionUse(string encounterId, ulong playerId, string characterId, string potionId, string? targetId, int turn)
        => WriteEvent(new { event_type = "potion_use", encounter = encounterId, potion = potionId, player = playerId, character = characterId, target = targetId, turn, run_id = _runId, timestamp = Now });

    public static void WriteCardExhaust(string encounterId, ulong playerId, string characterId, string cardId, bool fromEthereal, int turn, int upgradeLevel)
        => WriteEvent(new { event_type = "card_exhaust", encounter = encounterId, card = cardId, player = playerId, character = characterId, from_ethereal = fromEthereal, turn, upgrade_level = upgradeLevel, run_id = _runId, timestamp = Now });

    public static void WritePowerApplied(string encounterId, string powerId, string targetId, string? applierId, int amount, int turn)
        => WriteEvent(new { event_type = "power_applied", encounter = encounterId, power = powerId, target = targetId, applier = applierId, amount, turn, player = _localPlayerId, run_id = _runId, timestamp = Now });

    public static void WriteDamageDealt(string encounterId, string targetId, string? dealerId, int hpLost, int blocked, int overkill, int turn)
        => WriteEvent(new { event_type = "damage_dealt", encounter = encounterId, target = targetId, dealer = dealerId, hp_lost = hpLost, blocked, overkill, turn, player = _localPlayerId, run_id = _runId, timestamp = Now });

    public static void WriteBlockGained(string encounterId, string targetId, int amount, int turn)
        => WriteEvent(new { event_type = "block_gained", encounter = encounterId, target = targetId, amount, turn, player = _localPlayerId, run_id = _runId, timestamp = Now });

    public static void WriteOrbChanneled(string encounterId, ulong playerId, string characterId, string orbId, int turn)
        => WriteEvent(new { event_type = "orb_channeled", encounter = encounterId, player = playerId, character = characterId, orb = orbId, turn, run_id = _runId, timestamp = Now });

    public static void WriteStarsGained(string encounterId, ulong playerId, string characterId, int amount, int turn)
        => WriteEvent(new { event_type = "stars_gained", encounter = encounterId, player = playerId, character = characterId, amount, turn, run_id = _runId, timestamp = Now });

    public static void WriteMonsterAction(string encounterId, string monsterId, string moveId, List<string> intents, List<string> targets, int turn)
        => WriteEvent(new { event_type = "monster_action", encounter = encounterId, monster = monsterId, move = moveId, intents, targets, turn, player = _localPlayerId, run_id = _runId, timestamp = Now });

    public static void WriteRelicTriggered(string encounterId, string relicId, ulong playerId, List<string> targets, int turn)
        => WriteEvent(new { event_type = "relic_trigger", encounter = encounterId, relic = relicId, player = playerId, targets, turn, run_id = _runId, timestamp = Now });

    public static void WriteRoomEntered(string roomType, int floor, int act, ulong player)
        => WriteEvent(new { event_type = "room_entered", room_type = roomType, floor, act, player, run_id = _runId, timestamp = Now });

    public static void WriteRestSiteChoice(string optionId, int floor, ulong player)
        => WriteEvent(new { event_type = "rest_site_choice", option = optionId, floor, player, run_id = _runId, timestamp = Now });

    public static void WriteEventChoice(string eventId, string optionKey, int floor, ulong player)
        => WriteEvent(new { event_type = "event_choice", @event = eventId, option_key = optionKey, floor, player, run_id = _runId, timestamp = Now });

    public static void WriteShopPurchase(string itemType, string? itemId, int goldSpent, int floor, ulong player)
        => WriteEvent(new { event_type = "shop_purchase", item_type = itemType, item = itemId, gold_spent = goldSpent, floor, player, run_id = _runId, timestamp = Now });

    public static void WriteShopOffered(List<ShopItemSummary> items, int floor, ulong player)
        => WriteEvent(new { event_type = "shop_offered", items, floor, player, run_id = _runId, timestamp = Now });

    public static void WriteRewardsOffered(List<RewardSummary> rewards, int floor, ulong player)
        => WriteEvent(new { event_type = "rewards_offered", rewards, floor, player, run_id = _runId, timestamp = Now });

    public static void WriteRewardTaken(string rewardType, string? itemId, int? amount, int floor, ulong player)
    {
        if (amount.HasValue)
            WriteEvent(new { event_type = "reward_taken", reward_type = rewardType, item = itemId, amount = amount.Value, floor, player, run_id = _runId, timestamp = Now });
        else
            WriteEvent(new { event_type = "reward_taken", reward_type = rewardType, item = itemId, floor, player, run_id = _runId, timestamp = Now });
    }

    public static void WriteCombatEnd(string encounterId, string outcome)
        => WriteEvent(new { event_type = "combat_end", encounter = encounterId, outcome, player = _localPlayerId, run_id = _runId, timestamp = Now });

    // Called by CreateRunHistoryEntryPatch. Writes run_end, flushes all outputs,
    // renames the temp file, and resets all run state.
    public static void Finalize(long startTime, bool win, bool abandoned, string character, int ascension, int numPlayers)
    {
        try
        {
            WriteEvent(new { event_type = "run_end", win, abandoned, character, ascension, num_players = numPlayers, player = _localPlayerId, run_id = _runId, timestamp = Now });

            _writer?.Close();
            _writer = null;

            if (_tempFilePath != null && File.Exists(_tempFilePath))
            {
                string finalPath = GetHistoryFilePath($"{_runId}.{FileExtension}");
                File.Move(_tempFilePath, finalPath, overwrite: true);
                Log.Info($"[expanded-telemetry] Finalized telemetry to {finalPath}");
            }

            if (_sendToServer)
                RemoteSender.Flush(timeoutMs: 3000);
        }
        catch (Exception ex)
        {
            Log.Error("[expanded-telemetry] Failed to finalize telemetry stream: " + ex.Message + "\n" + ex.StackTrace);
        }
        finally
        {
            _isOpen = false;
            _writeToFile = false;
            _sendToServer = false;
            _localPlayerId = 0;
            _runId = 0;
            _writer = null;
            _tempFilePath = null;
        }
    }

    private static void WriteEvent(object evt)
    {
        if (!_isOpen) return;
        try
        {
            // Stamp every event with a fresh v4 UUID so the backend has a stable,
            // globally-unique dedup key. Injected centrally here rather than at each
            // call site: serialize to a node, prepend event_id, then re-serialize.
            var node = JsonSerializer.SerializeToNode(evt, evt.GetType())!.AsObject();
            node.Insert(0, "event_id", Guid.NewGuid().ToString());
            string json = node.ToJsonString();
            _writer?.WriteLine(json);
            if (_sendToServer) RemoteSender.Enqueue(json);
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
