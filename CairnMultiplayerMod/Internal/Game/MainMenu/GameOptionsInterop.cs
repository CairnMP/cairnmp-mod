using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;
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


    /// <summary>
    /// Applies a lobby's mode to the next new game: everyone launches under the same
    /// difficulty, with the same constraints.
    ///
    /// The values come from the game's own difficulty table rather than from constants of
    /// ours -- a mode names a difficulty, Cairn says what that difficulty is worth. Only the
    /// constraints the mode adds (a free-solo lobby forcing permadeath, say) are ours.
    /// Returns false while the menu or the difficulty table is not there yet; the caller
    /// retries, because the native New Game flow rewrites these options on entry.
    /// </summary>
    public static bool SetNextGameMode(MultiplayerModeRules rules, bool skipTutorials,
        bool skipPractice, bool assistEnabled, bool verbose = true)
    {
        try
        {
            var menu = FindMainMenuComponent();
            var opts = menu?.nextGameStartOptions;
            if (opts == null) return false;

            if (!NativeDifficultyCatalog.TryGetDefaults(rules.Difficulty, out var defaults))
            {
                LogMissingDifficultyOnce(rules);
                return false;
            }

            var constraints = defaults.Constraints
                              | (GamemodeConstraints)(int)rules.ExtraConstraints;

            var ng = opts.newGameOptions;
            ng.currentSelectedDifficulty = defaults.Difficulty;
            ng.customizedDifficulty = new GameSetup.CustomizedDifficulty(
                defaults.ClimbingPackage, defaults.SurvivalPackage, constraints);
            // Assist stays the host's call; the mode's constraints (NoAssistMode) are what
            // actually forbid it, and the game enforces those itself.
            ng.assistEnabled = assistEnabled;
            ng.skipTutorials = skipTutorials;
            ng.skipPractice = skipPractice;
            opts.newGameOptions = ng;

            if (verbose)
            {
                var check = opts.newGameOptions;
                ModLog.Info($"[GameOptions] Mode '{rules.Name}' applied: difficulty=" +
                    $"{check.currentSelectedDifficulty} constraints={constraints} " +
                    $"climbing={defaults.ClimbingPackage} survival={defaults.SurvivalPackage}");
            }
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Error($"[GameOptions] SetNextGameMode failed: {ex}");
            return false;
        }
    }

    private static GameDifficulty _missingDifficultyLogged = GameDifficulty.Invalid;

    private static void LogMissingDifficultyOnce(MultiplayerModeRules rules)
    {
        if (_missingDifficultyLogged == rules.Difficulty) return;
        _missingDifficultyLogged = rules.Difficulty;
        ModLog.Warning($"[GameOptions] The game's difficulty table has no entry for " +
            $"{rules.Difficulty} yet; mode '{rules.Name}' will be applied once it loads.");
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
