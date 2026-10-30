using System;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// What a Unity scene name means to the mod. Cairn streams a lot of additive scenes
/// (art, audio, LOD, holds...) on top of a single gameplay root, so "which scene are we
/// in" is a question about naming conventions — asked from the scene callbacks, the
/// bivouac gate and the player broadcaster alike. One home for the answers.
/// </summary>
internal static class SceneRoles
{
    public const string MainMenuScene = "MainMenu";
    private const string BivouacScene = "BivouacIndoor";
    private const string LoadingScene = "LoadingScreen";
    private const string CommonBaseScene = "CommonBaseScene";
    private const string SummitWorldPrefix = "Crux_";

    // Additive layers streamed on top of a gameplay root — never a root themselves.
    private static readonly string[] AdditiveSuffixes =
    {
        "_Holds", "_Gameplay", "_Art", "_Audio", "_Camera", "_LOD", "_AlwaysLoaded",
    };

    public static bool IsMainMenu(string sceneName)
        => string.Equals(sceneName, MainMenuScene, StringComparison.Ordinal);

    public static bool IsMainMenuArea(string sceneName)
        => sceneName != null && sceneName.StartsWith(MainMenuScene, StringComparison.Ordinal);

    public static bool IsLoading(string sceneName)
        => string.Equals(sceneName, LoadingScene, StringComparison.Ordinal)
        || string.Equals(sceneName, CommonBaseScene, StringComparison.Ordinal);

    /// <summary>The indoor bivouac: Cairn drives the pawn itself there, so the mod
    /// steps back to a minimal network presence.</summary>
    public static bool IsBivouac(string sceneName)
        => string.Equals(sceneName, BivouacScene, StringComparison.Ordinal);

    /// <summary>A story zone ("2_Kami") or a DLC world ("Crux_01_TowerGym"), never one of the
    /// layers streamed on top of either.</summary>
    public static bool IsGameplayRoot(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName)) return false;
        if (IsSummitWorldRoot(sceneName)) return true;
        if (!char.IsDigit(sceneName[0]))
            return false;

        if (sceneName.Contains("_Holds", StringComparison.Ordinal))
            return false;

        foreach (var suffix in AdditiveSuffixes)
        {
            if (sceneName.EndsWith(suffix, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The DLC worlds are Summit-mode scenes named "Crux_&lt;number&gt;_&lt;Name&gt;". Their
    /// layers add one more part ("Crux_01_TowerGym_Holds2", "..._Background"), so the root is
    /// the name made of exactly those three.
    /// </summary>
    private static bool IsSummitWorldRoot(string sceneName)
    {
        if (!sceneName.StartsWith(SummitWorldPrefix, StringComparison.Ordinal)) return false;
        var parts = sceneName.Split('_');
        if (parts.Length != 3 || parts[1].Length == 0 || parts[2].Length == 0) return false;
        foreach (var c in parts[1])
            if (!char.IsDigit(c)) return false;
        return true;
    }

    /// <summary>Whether a streamed layer still belongs to the gameplay world.</summary>
    public static bool HasGameplayContext(string currentScene, string lastGameplayScene)
    {
        if (IsGameplayRoot(currentScene)) return true;
        if (string.IsNullOrEmpty(lastGameplayScene)) return false;
        return !IsMainMenuArea(currentScene) && !IsLoading(currentScene) && !IsBivouac(currentScene);
    }

    /// <summary>Returns the stable gameplay scene used in network state. Cairn may load
    /// audio, art and other additive scenes after the root; those callbacks must not make
    /// players or world objects appear to have changed maps.</summary>
    public static string ResolveNetworkScene(string currentScene, string lastGameplayScene)
    {
        if (!string.IsNullOrEmpty(lastGameplayScene)
            && !IsGameplayRoot(currentScene)
            && !IsMainMenuArea(currentScene)
            && !IsLoading(currentScene))
            return lastGameplayScene;

        return currentScene ?? "";
    }

    /// <summary>
    /// Loading this scene invalidates everything bound to the previous one: the IL2CPP
    /// caches, the sync timers and the remote ghosts. The world pings survive on purpose
    /// — they sit in the world and expire on their own.
    /// </summary>
    public static bool IsSyncResetPoint(string sceneName)
        => IsLoading(sceneName) || IsMainMenu(sceneName) || IsGameplayRoot(sceneName);
}
