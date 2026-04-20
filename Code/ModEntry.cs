using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace ExpandedTelemetry;

[ModInitializer("Init")]
public static class ModEntry
{
    private static Harmony? _harmony;

    public static void Init()
    {
        Log.Warn("[expanded-telemetry] Initializing...");

        _harmony = new Harmony("com.blit.expandedtelemetry");
        _harmony.PatchAll();
        _harmony.GetPatchedMethods().ToList().ForEach(m => Log.Debug($"[expanded-telemetry] Patched method: {m.Name}"));

        Log.Warn("[expanded-telemetry] Loaded successfully!");
    }
}
