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

    // Additive layers streamed on top of a gameplay root — never a root themselves.
    private static readonly string[] AdditiveSuffixes =
    {
        "_Holds", "_Gameplay", "_Art", "_Audio", "_Camera", "_LOD", "_AlwaysLoaded",
    };

    public static bool IsMainMenu(string sceneName)
        => string.Equals(sceneName, MainMenuScene, StringComparison.Ordinal);

    /// <summary>Anywhere in the main-menu area, backdrops included
    /// ("MainMenu", "MainMenuBackgroundsBase"...).</summary>
    public static bool IsMainMenuArea(string sceneName)
        => sceneName != null && sceneName.StartsWith(MainMenuScene, StringComparison.Ordinal);

    /// <summary>The transition scenes shown between two gameplay roots.</summary>
    public static bool IsLoading(string sceneName)
        => string.Equals(sceneName, LoadingScene, StringComparison.Ordinal)
        || string.Equals(sceneName, CommonBaseScene, StringComparison.Ordinal);

    /// <summary>The indoor bivouac: Cairn drives the pawn itself there, so the mod
    /// steps back to a minimal network presence.</summary>
    public static bool IsBivouac(string sceneName)
        => string.Equals(sceneName, BivouacScene, StringComparison.Ordinal);

    /// <summary>A gameplay root scene (named "1_...", "2_..."), as opposed to the
    /// additive layers Cairn streams on top of it.</summary>
    public static bool IsGameplayRoot(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName) || !char.IsDigit(sceneName[0]))
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
