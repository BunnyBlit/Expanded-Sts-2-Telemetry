using System;
using System.IO;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace ExpandedTelemetry;

// Config is read once at mod init from a JSON file the user edits directly.
// File location: {OS.GetUserDataDir()}/mod_configs/expanded-telemetry.cfg
// e.g. ~/Library/Application Support/SlayTheSpire2/mod_configs/expanded-telemetry.cfg
internal class TelemetryConfig
{
    public bool WriteToFile { get; set; } = true;
    public bool SendToServer { get; set; } = false;

    // Server must accept POST requests with Content-Type: application/x-ndjson.
    // Events are batched and sent every ~200ms. Failures drop events, never block gameplay.
    public string ServerUrl { get; set; } = "";

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    public static TelemetryConfig Load()
    {
        try
        {
            string path = GetConfigPath();
            if (!File.Exists(path))
            {
                var defaults = new TelemetryConfig();
                WriteFile(defaults, path);
                Log.Info($"[expanded-telemetry] Created default config at {path}");
                return defaults;
            }
            var config = JsonSerializer.Deserialize<TelemetryConfig>(File.ReadAllText(path));
            if (config == null)
            {
                Log.Warn("[expanded-telemetry] Config was empty/invalid, using defaults");
                return new TelemetryConfig();
            }
            Log.Info($"[expanded-telemetry] Config loaded: WriteToFile={config.WriteToFile} SendToServer={config.SendToServer} ServerUrl={(string.IsNullOrEmpty(config.ServerUrl) ? "(unset)" : config.ServerUrl)}");
            return config;
        }
        catch (Exception ex)
        {
            Log.Error("[expanded-telemetry] Failed to load config, using defaults: " + ex.Message);
            return new TelemetryConfig();
        }
    }

    private static string GetConfigPath()
        => Path.Combine(OS.GetUserDataDir(), "mod_configs", "expanded-telemetry.cfg");

    private static void WriteFile(TelemetryConfig config, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, _jsonOpts));
    }
}
