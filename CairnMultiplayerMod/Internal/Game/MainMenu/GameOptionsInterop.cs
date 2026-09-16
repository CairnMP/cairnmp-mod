using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.MainMenu;

internal static unsafe class GameOptionsInterop
{
    /// <summary>
    /// The native New Game flow resets skip flags, but difficulty must remain each player's choice.
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
                ModLog.Debug($"[GameOptions] NewGameOptions skip-only set: skipTut={check.skipTutorials} " +
                    $"skipPra={check.skipPractice} assist={check.assistEnabled} (difficulty preserved={check.currentSelectedDifficulty})");
            }
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Error($"[GameOptions] SetNextGameSkipOptions failed: {ex}");
            return false;
        }
    }

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
