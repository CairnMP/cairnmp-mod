using MelonLoader;

namespace CairnMultiplayerMod.Config;

public static class ModConfig
{
    private static readonly string CategoryPlayer   = "Player";
    private static readonly string CategoryRoom     = "Room";
    private static readonly string CategoryKeybinds = "Keybinds";
    private static readonly string CategoryDebug    = "Debug";

    // Player
    public static MelonPreferences_Entry<string> PlayerName;

    // Room
    public static MelonPreferences_Entry<string> RoomCode;
    public static MelonPreferences_Entry<int>    MaxPlayers;

    // Keybinds
    public static MelonPreferences_Entry<string> ConnectKey;
    public static MelonPreferences_Entry<string> DisconnectKey;

    // Debug
    public static MelonPreferences_Entry<bool>   VerboseLogging;

    public static void Register()
    {
        var player = MelonPreferences.CreateCategory(CategoryPlayer);
        PlayerName = player.CreateEntry("Name", "Player");

        var room = MelonPreferences.CreateCategory(CategoryRoom);
        RoomCode   = room.CreateEntry("Code", "");
        MaxPlayers = room.CreateEntry("MaxPlayers", 8);

        var keybinds = MelonPreferences.CreateCategory(CategoryKeybinds);
        ConnectKey    = keybinds.CreateEntry("ConnectKey", "F5");
        DisconnectKey = keybinds.CreateEntry("DisconnectKey", "F6");

        var debug = MelonPreferences.CreateCategory(CategoryDebug);
        VerboseLogging = debug.CreateEntry("VerboseLogging", false);
    }
}
