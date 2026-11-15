using System;
using BepInEx.Logging;
using MiniJSON;

// NativeOptionsPanels owns the native plank and its measured layout, just like Pace.
internal static class NativeOptionsDifficulty
{
    internal const string Id = "options.difficulty", ObjectName = "SoloStoneDifficultyOption";
    private static ManualLogSource log;
    private static UIBButton button;

    internal static bool Available => log != null;
    internal static void Initialize(ManualLogSource logger) { log = logger; }
    internal static bool Wanted() => NativeOptionsPace.Wanted();

    internal static void Attach(UIBButton plank)
    {
        NativeButtonKit.Fit(plank, 1f);
        plank.AddEventListener(UIEventType.RELEASE, _ => Open());
        button = plank;
        Sync();
    }

    internal static void Sync()
    {
        try { if (button != null && Wanted()) button.SetText("NPC Difficulty: " + NpcDifficultyIds.Name(LocalLadder.Profile.NpcDifficulty)); }
        catch (Exception error) { log?.LogWarning("OPTIONS_DIFFICULTY sync: " + error.Message); }
    }

#if !SOLOSTONE_RELEASE
    internal static JsonNode State()
    {
        var node = NativeOptionsPanels.RowState(button);
        node["wanted"] = Available && Wanted();
        return node;
    }
#endif

    private static void Open()
    {
        try
        {
            Sync();
            if (!Wanted()) return;
            NativeOptionsPanels.LeaveForPace();
            NativeProfileSelector.OpenDifficulty();
        }
        catch (Exception error) { log?.LogWarning("OPTIONS_DIFFICULTY open: " + error.Message); }
    }
}
