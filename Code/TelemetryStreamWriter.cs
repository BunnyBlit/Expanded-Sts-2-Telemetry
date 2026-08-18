using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
    private static long _runId;                  // Unix seconds — run start time; used for file naming + resume
    private static int _ascension;               // ascension level of the run; emitted on run_start
    private static string _runUuid = "";         // v5 UUID derived from _runId — emitted as run_id
    private static string _localPlayerUuid = ""; // v5 UUID derived from _localPlayerId — emitted as player_id
    private static long _seq;                     // per-run monotonic sequence, breaks same-second ties downstream
    private static StreamWriter? _writer;
    private static string? _tempFilePath;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // Fresh v4 UUID per event — the backend's globally-unique dedup key.
    // Stamped as the first field on every event object below.
    private static string NewEventId => Guid.NewGuid().ToString();

    // Per-run monotonic sequence number stamped on every event, in emission order.
    // Fresh runs start at 0; on resume Open() recovers the last value (RecoverLastSeq) so it
    // stays monotonic across a save+quit -> Continue. Downstream orders a run by seq_num
    // (and can fall back to timestamp if a resume ever restarts it at 0 — e.g. server-only mode).
    private static long NextSeq() => Interlocked.Increment(ref _seq);

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
    private sealed record PlayerStateEntry(string player_id, string character, int hp, int max_hp, int block, int energy, int max_energy, List<PowerEntry> powers);
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
            var runSave = RunManager.Instance.ToSave(null);
            _runId = runSave.StartTime;
            _ascension = runSave.Ascension;
            _localPlayerUuid = TelemetryId.Player(_localPlayerId);
            _runUuid = TelemetryId.Run(_runId);
            _seq = 0;

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
                    RemoteSender.Start(config.ServerUrl, config.AuthToken);
                }
            }

            if (!_writeToFile && !_sendToServer)
                Log.Warn("[expanded-telemetry] Both WriteToFile and SendToServer are disabled — no telemetry will be recorded.");

            bool resuming = false;
            if (_writeToFile)
            {
                _tempFilePath = GetHistoryFilePath($"{_runId}.in_progress.{FileExtension}");
                Directory.CreateDirectory(Path.GetDirectoryName(_tempFilePath)!);

                // Resume detection. A hard crash leaves a `{run_id}.in_progress` file; a
                // graceful save+quit runs CreateRunHistoryEntry -> Finalize, which renames
                // it to the finalized `{run_id}.expanded_run`. Either means this run_id
                // already has data we must CONTINUE, not overwrite. Reopen a finalized file
                // by renaming it back to in-progress so appends (and the eventual re-finalize)
                // target the same file instead of clobbering it.
                string finalPath = GetHistoryFilePath($"{_runId}.{FileExtension}");
                bool tempExisted = File.Exists(_tempFilePath);
                bool finalExisted = File.Exists(finalPath);
                if (!tempExisted && finalExisted)
                    File.Move(finalPath, _tempFilePath);

                resuming = File.Exists(_tempFilePath);
                if (resuming)
                {
                    // Continue the sequence counter so seq_num stays monotonic across the
                    // resume (the last line holds the highest seq written so far).
                    _seq = RecoverLastSeq(_tempFilePath);
                    Log.Info($"[expanded-telemetry] Resuming run {_runId} at seq {_seq}, appending to existing file.");
                }
                _writer = new StreamWriter(_tempFilePath, append: resuming) { AutoFlush = true };
            }

            if (!resuming)
            {
                string gameVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "dev";
                int profileId = SaveManager.Instance.CurrentProfileId;
                // Local player + roster are fixed before the first room is entered (RunState is
                // built at character-select and passed into RunManager setup), so character/count
                // are stable here — same source and resolution as run_end (CreateRunHistoryEntryPatch).
                var localPlayer = runSave.Players.Find(p => p.NetId == _localPlayerId) ?? runSave.Players[0];
                string character = localPlayer.CharacterId?.Entry ?? string.Empty;
                int numPlayers = runSave.Players.Count;
                WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "run_start", player_id = _localPlayerUuid, game_version = gameVersion, profile = profileId, ascension = _ascension, character, num_players = numPlayers, run_id = _runUuid, timestamp = Now });
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
            _runUuid = "";
            _localPlayerUuid = "";
            _seq = 0;
            _writer = null;
            _tempFilePath = null;
        }
    }

    public static void WriteCombatStart(string encounterId)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "combat_start", encounter = encounterId, player_id = _localPlayerUuid, run_id = _runUuid, timestamp = Now });

    public static void WriteTurnStart(string encounterId, int turn, IReadOnlyList<Player> players, IReadOnlyList<Creature> enemies)
    {
        var playerStates = players.Select(p => new PlayerStateEntry(
            player_id: _localPlayerUuid,
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
        WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "turn_start", encounter = encounterId, turn, player_id = _localPlayerUuid, players = playerStates, monsters = monsterStates, run_id = _runUuid, timestamp = Now });
    }

    public static void WriteTurnEnd(string encounterId, int turn)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "turn_end", encounter = encounterId, turn, player_id = _localPlayerUuid, run_id = _runUuid, timestamp = Now });

    public static void WriteCardPlay(string encounterId, ulong playerId, string characterId, string cardId, string? targetId, int turn, int upgradeLevel, bool isAutoPlay)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "card_play", encounter = encounterId, card = cardId, player_id = _localPlayerUuid, character = characterId, target = targetId, turn, upgrade_level = upgradeLevel, is_auto_play = isAutoPlay, run_id = _runUuid, timestamp = Now });

    public static void WriteCardDraw(string encounterId, ulong playerId, string characterId, string cardId, bool fromHandDraw, int turn, int upgradeLevel)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "card_draw", encounter = encounterId, card = cardId, player_id = _localPlayerUuid, character = characterId, from_hand_draw = fromHandDraw, turn, upgrade_level = upgradeLevel, run_id = _runUuid, timestamp = Now });

    public static void WriteCardDiscard(string encounterId, ulong playerId, string characterId, string cardId, bool fromFlush, int turn, int upgradeLevel)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "card_discard", encounter = encounterId, card = cardId, player_id = _localPlayerUuid, character = characterId, from_flush = fromFlush, turn, upgrade_level = upgradeLevel, run_id = _runUuid, timestamp = Now });

    public static void WritePotionUse(string encounterId, ulong playerId, string characterId, string potionId, string? targetId, int turn)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "potion_use", encounter = encounterId, potion = potionId, player_id = _localPlayerUuid, character = characterId, target = targetId, turn, run_id = _runUuid, timestamp = Now });

    public static void WriteCardExhaust(string encounterId, ulong playerId, string characterId, string cardId, bool fromEthereal, int turn, int upgradeLevel)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "card_exhaust", encounter = encounterId, card = cardId, player_id = _localPlayerUuid, character = characterId, from_ethereal = fromEthereal, turn, upgrade_level = upgradeLevel, run_id = _runUuid, timestamp = Now });

    public static void WritePowerApplied(string encounterId, string powerId, string targetId, string? applierId, int amount, int turn)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "power_applied", encounter = encounterId, power = powerId, target = targetId, applier = applierId, amount, turn, player_id = _localPlayerUuid, run_id = _runUuid, timestamp = Now });

    public static void WriteDamageDealt(string encounterId, string targetId, string? dealerId, int hpLost, int blocked, int overkill, int turn)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "damage_dealt", encounter = encounterId, target = targetId, dealer = dealerId, hp_lost = hpLost, blocked, overkill, turn, player_id = _localPlayerUuid, run_id = _runUuid, timestamp = Now });

    public static void WriteBlockGained(string encounterId, string targetId, int amount, int turn)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "block_gained", encounter = encounterId, target = targetId, amount, turn, player_id = _localPlayerUuid, run_id = _runUuid, timestamp = Now });

    public static void WriteOrbChanneled(string encounterId, ulong playerId, string characterId, string orbId, int turn)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "orb_channeled", encounter = encounterId, player_id = _localPlayerUuid, character = characterId, orb = orbId, turn, run_id = _runUuid, timestamp = Now });

    public static void WriteStarsGained(string encounterId, ulong playerId, string characterId, int amount, int turn)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "stars_gained", encounter = encounterId, player_id = _localPlayerUuid, character = characterId, amount, turn, run_id = _runUuid, timestamp = Now });

    public static void WriteMonsterAction(string encounterId, string monsterId, string moveId, List<string> intents, List<string> targets, int turn)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "monster_action", encounter = encounterId, monster = monsterId, move = moveId, intents, targets, turn, player_id = _localPlayerUuid, run_id = _runUuid, timestamp = Now });

    public static void WriteRelicTriggered(string encounterId, string relicId, ulong playerId, List<string> targets, int turn)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "relic_trigger", encounter = encounterId, relic = relicId, player_id = _localPlayerUuid, targets, turn, run_id = _runUuid, timestamp = Now });

    public static void WriteRoomEntered(string roomType, int floor, int act, ulong player)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "room_entered", room_type = roomType, floor, act, player_id = _localPlayerUuid, run_id = _runUuid, timestamp = Now });

    public static void WriteRestSiteChoice(string optionId, int floor, ulong player)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "rest_site_choice", option = optionId, floor, player_id = _localPlayerUuid, run_id = _runUuid, timestamp = Now });

    public static void WriteEventChoice(string eventId, string optionKey, int floor, ulong player)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "event_choice", @event = eventId, option_key = optionKey, floor, player_id = _localPlayerUuid, run_id = _runUuid, timestamp = Now });

    public static void WriteShopPurchase(string itemType, string? itemId, int goldSpent, int floor, ulong player)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "shop_purchase", item_type = itemType, item = itemId, gold_spent = goldSpent, floor, player_id = _localPlayerUuid, run_id = _runUuid, timestamp = Now });

    public static void WriteShopOffered(List<ShopItemSummary> items, int floor, ulong player)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "shop_offered", items, floor, player_id = _localPlayerUuid, run_id = _runUuid, timestamp = Now });

    public static void WriteRewardsOffered(List<RewardSummary> rewards, int floor, ulong player)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "rewards_offered", rewards, floor, player_id = _localPlayerUuid, run_id = _runUuid, timestamp = Now });

    public static void WriteRewardTaken(string rewardType, string? itemId, int? amount, int floor, ulong player)
    {
        if (amount.HasValue)
            WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "reward_taken", reward_type = rewardType, item = itemId, amount = amount.Value, floor, player_id = _localPlayerUuid, run_id = _runUuid, timestamp = Now });
        else
            WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "reward_taken", reward_type = rewardType, item = itemId, floor, player_id = _localPlayerUuid, run_id = _runUuid, timestamp = Now });
    }

    public static void WriteCombatEnd(string encounterId, string outcome)
        => WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "combat_end", encounter = encounterId, outcome, player_id = _localPlayerUuid, run_id = _runUuid, timestamp = Now });

    // Called by CreateRunHistoryEntryPatch. Writes run_end, flushes all outputs,
    // renames the temp file, and resets all run state.
    public static void Finalize(long startTime, bool win, bool abandoned, string character, int ascension, int numPlayers)
    {
        try
        {
            WriteEvent(new { event_id = NewEventId, seq_num = NextSeq(), event_type = "run_end", win, abandoned, character, ascension, num_players = numPlayers, player_id = _localPlayerUuid, run_id = _runUuid, timestamp = Now });

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
            _runUuid = "";
            _localPlayerUuid = "";
            _seq = 0;
            _writer = null;
            _tempFilePath = null;
        }
    }

    private static void WriteEvent(object evt)
    {
        if (!_isOpen) return;
        try
        {
            string json = JsonSerializer.Serialize(evt, evt.GetType());
            _writer?.WriteLine(json);
            if (_sendToServer) RemoteSender.Enqueue(json);
        }
        catch (Exception ex)
        {
            Log.Error("[expanded-telemetry] Failed to write telemetry event: " + ex.Message);
        }
    }

    // On resume, read the highest seq_num already written so NextSeq() continues from
    // there. seq_num is monotonic in emission order, so the last non-empty line holds the
    // max. Returns 0 (seq restarts) if the file is empty/unparseable — safe, just degrades
    // ordering across that one boundary to (timestamp, seq).
    private static long RecoverLastSeq(string path)
    {
        try
        {
            string? last = null;
            foreach (string line in File.ReadLines(path))
                if (!string.IsNullOrWhiteSpace(line)) last = line;
            if (last == null) return 0;
            using var doc = JsonDocument.Parse(last);
            if (doc.RootElement.TryGetProperty("seq_num", out var s) && s.TryGetInt64(out long v))
                return v;
        }
        catch (Exception ex)
        {
            Log.Warn("[expanded-telemetry] Could not recover seq_num on resume, restarting at 0: " + ex.Message);
        }
        return 0;
    }

    // Telemetry files live OUTSIDE the game's run-history directory. That directory
    // (steam/{id}/modded/profileN/saves/history) is backed by a CloudSaveStore that treats
    // the cloud as authoritative: on sync, any file present locally but NOT in Steam Cloud is
    // deleted (CloudSaveStore.SyncCloudToLocalInternal: `LocalStore.DeleteFile(path)`). The
    // game's own `.run` files survive because it writes them THROUGH the cloud store (so they
    // exist in the cloud); our files are written with a raw StreamWriter, bypassing the cloud
    // store, so they're local-only and the sync was silently deleting them between a save+quit
    // and the resume. OS.GetUserDataDir() (the same base as the config file at mod_configs/) is
    // not cloud-managed, so our files persist — which also lets the resume logic above find the
    // prior file. Scoped by account + profile to mirror the old layout; the raw Steam id appears
    // only in this local path, never in emitted events.
    private static string GetHistoryFilePath(string fileName)
    {
        int profileId = SaveManager.Instance.CurrentProfileId;
        string dir = Path.Combine(OS.GetUserDataDir(), "expanded_telemetry", _localPlayerId.ToString(), $"profile{profileId}");
        return Path.Combine(dir, fileName);
    }
}
