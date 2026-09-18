using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;

namespace CairnMultiplayerMod.Internal.Game.MainMenu;

/// <summary>What the game itself says a difficulty means.
///
/// Cairn keeps its difficulties in <c>DifficultyTweakables.modes</c>: each one carries the
/// constraint flags, the climbing and survival packages and the assist defaults that the New
/// Game screen would apply. A multiplayer mode names a difficulty and lets the game fill in
/// the rest, so a lobby plays by the same numbers as a solo run of that difficulty — and
/// keeps doing so if a patch rebalances them.
/// </summary>
internal readonly struct NativeDifficultyDefaults
{
    internal NativeDifficultyDefaults(DifficultyTweakables.SelectedDifficulty difficulty,
        GamemodeConstraints constraints, DifficultyTweakables.PackageType climbingPackage,
        DifficultyTweakables.PackageType survivalPackage, bool assistEnabled)
    {
        Difficulty = difficulty;
        Constraints = constraints;
        ClimbingPackage = climbingPackage;
        SurvivalPackage = survivalPackage;
        AssistEnabled = assistEnabled;
    }

    internal DifficultyTweakables.SelectedDifficulty Difficulty { get; }
    internal GamemodeConstraints Constraints { get; }
    internal DifficultyTweakables.PackageType ClimbingPackage { get; }
    internal DifficultyTweakables.PackageType SurvivalPackage { get; }
    internal bool AssistEnabled { get; }
}

internal static class NativeDifficultyCatalog
{
    internal static bool TryGetDefaults(GameDifficulty difficulty, out NativeDifficultyDefaults defaults)
    {
        defaults = default;
        var wanted = (DifficultyTweakables.SelectedDifficulty)(int)difficulty;
        if (wanted == DifficultyTweakables.SelectedDifficulty.Invalid) return false;

        try
        {
            if (!TweakableBase<DifficultyTweakables>.IsReady) return false;
            var modes = TweakableBase<DifficultyTweakables>.Instance?.modes;
            if (modes == null) return false;

            for (var index = 0; index < modes.Length; index++)
            {
                var mode = modes[index];
                if (mode == null || mode.difficulty != wanted) continue;

                var values = mode.defaults;
                defaults = new NativeDifficultyDefaults(wanted, values.GamemodeConstraints,
                    values.climbingPackageType, values.survivalPackageType, values.assist);
                return true;
            }
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[Modes] Could not read the game's difficulty table: {exception.Message}");
        }
        return false;
    }

    /// <summary>True while the game's difficulty table can be read — it loads with the
    /// tweakables, so a lobby created during the boot sequence must wait for it.</summary>
    internal static bool IsReady
    {
        get
        {
            try { return TweakableBase<DifficultyTweakables>.IsReady; }
            catch (Exception exception)
            {
                ModLog.SuppressedException("modes.difficulty-table-ready", exception);
                return false;
            }
        }
    }
}
