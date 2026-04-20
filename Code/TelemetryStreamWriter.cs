using System;
using System.IO;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace ExpandedTelemetry;

// Streams telemetry events to disk as NDJSON (one JSON object per line) while a run
// is in progress. The file is written to `in_progress.encounter_cards` and renamed to
// `{StartTime}.encounter_cards` when the run ends, matching the game's .run file naming.
internal static class TelemetryStreamWriter
{
    private const string TempFileName = "in_progress.encounter_cards";

    private static StreamWriter? _writer;
    private static string? _tempFilePath;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

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

    public static void WriteTurnStart(string encounterId, int turn)
        => WriteEvent(new { event_type = "turn_start", encounter = encounterId, turn, timestamp = Now });

    public static void WriteTurnEnd(string encounterId, int turn)
        => WriteEvent(new { event_type = "turn_end", encounter = encounterId, turn, timestamp = Now });

    public static void WriteCardPlay(string encounterId, ulong playerId, string cardId, string? targetId, int turn, int upgradeLevel, bool isAutoPlay)
        => WriteEvent(new { event_type = "card_play", encounter = encounterId, card = cardId, player = playerId, target = targetId, turn, upgrade_level = upgradeLevel, is_auto_play = isAutoPlay, timestamp = Now });

    public static void WriteCardDraw(string encounterId, ulong playerId, string cardId, bool fromHandDraw, int turn, int upgradeLevel)
        => WriteEvent(new { event_type = "card_draw", encounter = encounterId, card = cardId, player = playerId, from_hand_draw = fromHandDraw, turn, upgrade_level = upgradeLevel, timestamp = Now });

    public static void WriteCardDiscard(string encounterId, ulong playerId, string cardId, bool fromFlush, int turn, int upgradeLevel)
        => WriteEvent(new { event_type = "card_discard", encounter = encounterId, card = cardId, player = playerId, from_flush = fromFlush, turn, upgrade_level = upgradeLevel, timestamp = Now });

    public static void WritePotionUse(string encounterId, ulong playerId, string potionId, string? targetId, int turn)
        => WriteEvent(new { event_type = "potion_use", encounter = encounterId, potion = potionId, player = playerId, target = targetId, turn, timestamp = Now });

    public static void WriteCardExhaust(string encounterId, ulong playerId, string cardId, bool fromEthereal, int turn, int upgradeLevel)
        => WriteEvent(new { event_type = "card_exhaust", encounter = encounterId, card = cardId, player = playerId, from_ethereal = fromEthereal, turn, upgrade_level = upgradeLevel, timestamp = Now });

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
