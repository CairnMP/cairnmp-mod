using System;
using CairnMultiplayer.Shared;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.GameApi;

/// <summary>Pre-sets the options for the next new game (difficulty, skip tutorials/practice,
/// assist) on the native MainMenu, via the typed Il2CppInterop API.</summary>
internal static unsafe class GameOptionsApi
{
    /// <summary>
    /// Pre-sets the options for the next new game (difficulty, skip tutorials/practice,
    /// assist) on the native MainMenu, via the TYPED Il2CppInterop API (no pointer arithmetic).
    ///
    /// nextGameStartOptions.newGameOptions is a STRUCT (NewGameLaunchOptions): we read a
    /// copy, modify the fields, then write it back via the setter. GameDifficulty (Shared) already
    /// has the same hashed values as the native SelectedDifficulty -> direct cast.
    /// </summary>
    public static bool SetNextGameDifficulty(GameDifficulty difficulty,
        bool skipTutorials, bool skipPractice, bool assistEnabled, bool verbose = true)
    {
        try
        {
            var menu = FindMainMenuComponent();
            if (menu == null)
            {
                Mod.Log.Warning("[GameOptions] MainMenu component not found");
                return false;
            }

            var opts = menu.nextGameStartOptions;
            if (opts == null)
            {
                Mod.Log.Warning("[GameOptions] nextGameStartOptions is null on MainMenu");
                return false;
            }

            // Struct -> local copy, modify, write back via the setter.
            var ng = opts.newGameOptions;
            var targetDifficulty = (DifficultyTweakables.SelectedDifficulty)(int)difficulty;

            // CRUCIAL idempotence: this setter is called EVERY frame while the native
            // save menu is open (StartGameFlow). Rewriting the same value makes the
            // game emit a "difficulty changed" notification in a loop. So we only rewrite if
            // at least one field actually differs (typically after the native flow has
            // reset newGameOptions to its defaults on the "new game" click).
            if (ng.currentSelectedDifficulty == targetDifficulty
                && ng.skipTutorials == skipTutorials
                && ng.skipPractice == skipPractice
                && ng.assistEnabled == assistEnabled)
            {
                return true;
            }

            ng.skipTutorials = skipTutorials;
            ng.skipPractice = skipPractice;
            ng.assistEnabled = assistEnabled;
            ng.currentSelectedDifficulty = targetDifficulty;
            opts.newGameOptions = ng;

            if (verbose)
            {
                // Read back to confirm (Il2Cpp structs can surprise you).
                var check = opts.newGameOptions;
                Mod.LogDebug($"[GameOptions] NewGameOptions set (typed): difficulty={difficulty} " +
                    $"skipTut={check.skipTutorials} skipPra={check.skipPractice} assist={check.assistEnabled}");
            }
            return true;
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[GameOptions] SetNextGameDifficulty failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Variant of <see cref="SetNextGameDifficulty"/> that forces ONLY the
    /// skip tutorials/practice + assist flags, while PRESERVING the difficulty chosen by the player
    /// in the native screen. Used by the launch flow when we want to respect the native
    /// difficulty choice (e.g. FreeRoam) instead of forcing it: the native flow resets the
    /// skip flags on every "new game", so we re-apply them continuously, but without
    /// ever rewriting currentSelectedDifficulty.
    /// </summary>
    public static bool SetNextGameSkipOptions(bool skipTutorials, bool skipPractice,
        bool assistEnabled, bool verbose = true)
    {
        try
        {
            var menu = FindMainMenuComponent();
            if (menu == null) return false;

            var opts = menu.nextGameStartOptions;
            if (opts == null) return false;

            var ng = opts.newGameOptions;

            // Idempotence: only rewrite if a skip/assist flag differs (the difficulty
            // is never compared or touched — it stays the player's choice).
            if (ng.skipTutorials == skipTutorials
                && ng.skipPractice == skipPractice
                && ng.assistEnabled == assistEnabled)
            {
                return true;
            }

            ng.skipTutorials = skipTutorials;
            ng.skipPractice = skipPractice;
            ng.assistEnabled = assistEnabled;
            opts.newGameOptions = ng;

            if (verbose)
            {
                var check = opts.newGameOptions;
                Mod.LogDebug($"[GameOptions] NewGameOptions skip-only set: skipTut={check.skipTutorials} " +
                    $"skipPra={check.skipPractice} assist={check.assistEnabled} (difficulty preserved={check.currentSelectedDifficulty})");
            }
            return true;
        }
        catch (Exception ex)
        {
            Mod.Log.Error($"[GameOptions] SetNextGameSkipOptions failed: {ex}");
            return false;
        }
    }

    /// <summary>Finds the active MainMenu (UI) component in the scene.</summary>
    private static Il2CppTheGameBakers.Cairn.UI.MainMenu FindMainMenuComponent()
    {
        var menuGo = GameObject.Find("MainMenu");
        if (menuGo == null) return null;

        var menu = menuGo.GetComponent<Il2CppTheGameBakers.Cairn.UI.MainMenu>();
        if (menu != null) return menu;

        // Fallback: iterate the components with TryCast (depending on the Il2Cpp type registration).
        var components = menuGo.GetComponents<MonoBehaviour>();
        for (int i = 0; i < components.Count; i++)
        {
            var cast = components[i] != null
                ? components[i].TryCast<Il2CppTheGameBakers.Cairn.UI.MainMenu>() : null;
            if (cast != null) return cast;
        }
        return null;
    }
}
