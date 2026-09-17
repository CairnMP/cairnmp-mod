using MelonLoader;

namespace CairnMultiplayerMod.Internal;

internal static class ModConfig
{
    private static readonly string CategoryPlayer = "Player";
    private static readonly string CategoryRoom = "Room";
    private static readonly string CategoryKeybinds = "Keybinds";
    private static readonly string CategoryDebug = "Debug";

    public static MelonPreferences_Entry<string> PlayerName;

    public static MelonPreferences_Entry<string> RoomCode;
    public static MelonPreferences_Entry<int> MaxPlayers;

    public static MelonPreferences_Entry<string> ConnectKey;
    public static MelonPreferences_Entry<string> DisconnectKey;

    public static MelonPreferences_Entry<bool> VerboseLogging;
    public static MelonPreferences_Entry<bool> PerformanceDiagnostics;
    public static MelonPreferences_Entry<bool> NativeProfiling;
    public static MelonPreferences_Entry<string> PerformanceScenario;

    public static void Register()
    {
        var player = MelonPreferences.CreateCategory(CategoryPlayer);
        PlayerName = player.CreateEntry("Name", "Player");

        var room = MelonPreferences.CreateCategory(CategoryRoom);
        RoomCode = room.CreateEntry("Code", "");
        MaxPlayers = room.CreateEntry("MaxPlayers", 8);

        var keybinds = MelonPreferences.CreateCategory(CategoryKeybinds);
        ConnectKey = keybinds.CreateEntry("ConnectKey", "F5");
        DisconnectKey = keybinds.CreateEntry("DisconnectKey", "F6");

        var debug = MelonPreferences.CreateCategory(CategoryDebug);
        VerboseLogging = debug.CreateEntry("VerboseLogging", false);
        PerformanceDiagnostics = debug.CreateEntry("PerformanceDiagnostics", false);
        // Times the native methods flagged by the reverse-engineering audit. Adds Harmony
        // hooks to per-frame game code, so it stays off unless someone is measuring.
        NativeProfiling = debug.CreateEntry("NativeProfiling", false);
        PerformanceScenario = debug.CreateEntry("PerformanceScenario", "unspecified - set route, save, run number and voice/rope state");
    }
}
