using MelonLoader;

namespace CairnMultiplayerMod.Core;

public static class ModConfig
{
    private static readonly string CategoryApi      = "API";
    private static readonly string CategoryPlayer   = "Player";
    private static readonly string CategoryRoom     = "Room";
    private static readonly string CategoryKeybinds = "Keybinds";

    // API
    public static MelonPreferences_Entry<string> ApiBaseUrl;
    public static MelonPreferences_Entry<string> PreferredRegion;

    // Joueur
    public static MelonPreferences_Entry<string> PlayerName;

    // Salon
    public static MelonPreferences_Entry<string> RoomCode;
    public static MelonPreferences_Entry<int>    Gamemode;
    public static MelonPreferences_Entry<int>    MaxPlayers;

    // Raccourcis clavier
    public static MelonPreferences_Entry<string> ConnectKey;
    public static MelonPreferences_Entry<string> DisconnectKey;

    public static void Register()
    {
        var api = MelonPreferences.CreateCategory(CategoryApi);
        ApiBaseUrl      = api.CreateEntry("ApiBaseUrl", "https://api.cairnmultiplayer.com");
        PreferredRegion = api.CreateEntry("PreferredRegion", "auto");

        var player = MelonPreferences.CreateCategory(CategoryPlayer);
        PlayerName = player.CreateEntry("Name", "Player");

        var room = MelonPreferences.CreateCategory(CategoryRoom);
        RoomCode   = room.CreateEntry("Code", "");
        Gamemode   = room.CreateEntry("Gamemode", 3);
        MaxPlayers = room.CreateEntry("MaxPlayers", 8);

        var keybinds = MelonPreferences.CreateCategory(CategoryKeybinds);
        ConnectKey    = keybinds.CreateEntry("ConnectKey", "F5");
        DisconnectKey = keybinds.CreateEntry("DisconnectKey", "F6");

        MigrateLegacyApiUrl();
    }

    // Migration : l'API a été renommée gateway → api.cairnmultiplayer.com.
    // Les configs existants pointent encore vers l'ancien domaine — on les réécrit.
    private static void MigrateLegacyApiUrl()
    {
        const string LegacyApiUrl  = "https://gateway.cairnmultiplayer.com";
        const string CurrentApiUrl = "https://api.cairnmultiplayer.com";

        var current = ApiBaseUrl.Value?.TrimEnd('/');
        if (!string.Equals(current, LegacyApiUrl, System.StringComparison.OrdinalIgnoreCase))
            return;

        ApiBaseUrl.Value = CurrentApiUrl;
        MelonPreferences.Save();
        MelonLogger.Msg($"[ModConfig] Migrated ApiBaseUrl from {LegacyApiUrl} to {CurrentApiUrl}");
    }
}
