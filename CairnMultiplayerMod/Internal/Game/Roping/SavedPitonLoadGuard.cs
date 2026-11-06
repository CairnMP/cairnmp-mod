using System;
using System.Reflection;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using Savegame = Il2CppTheGameBakers.Cairn.Save.Latest.Savegame;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Saves written while roped by builds before 2.3.2 hold pitons with no valid climber pose.
/// Cairn warns about each one and then loads on without a securing rope, and the engine
/// dies a moment later. Skipping the game's replay of those pitons lets the rest of the save in.
/// </summary>
internal static class SavedPitonLoadGuard
{
    private static readonly HarmonyLib.Harmony LoadGuardHarmony = new("CairnMultiplayerMod.SavedPitonLoadGuard");
    private static bool _installed;
    private static int _skipped;

    public static void Install()
    {
        if (_installed) return;

        var target = FindReplayMethod();
        var prefix = typeof(SavedPitonLoadGuard).GetMethod(
            nameof(ReplayPitonPrefix),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        if (target == null || prefix == null)
        {
            ModLog.Warning("[LoadGuard] The game's saved-piton replay was not found — saved pitons are not checked.");
            return;
        }

        LoadGuardHarmony.Patch(target, prefix: new HarmonyMethod(prefix));
        _installed = true;
        ModLog.Info("[LoadGuard] Saved pitons without a climber pose will be skipped on load.");
    }

    public static void Uninstall()
    {
        try { LoadGuardHarmony.UnpatchSelf(); }
        finally
        {
            _installed = false;
            _skipped = 0;
        }
    }

    internal static bool ShouldSkip(bool hasValidPose) => !hasValidPose;

    /// <summary>
    /// The game's own replay of one saved piton, a compiler-generated local function of
    /// PawnControllerSwitcher.ReadFromSavegame. Its generated name is not stable, so it is
    /// found by the saved piton it takes by reference.
    /// </summary>
    private static MethodBase FindReplayMethod()
    {
        var closure = AccessTools.Inner(typeof(PawnControllerSwitcher), "__c__DisplayClass167_0");
        if (closure == null) return null;

        var savedPiton = typeof(Savegame.World.Piton).MakeByRefType();
        foreach (var method in closure.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            var parameters = method.GetParameters();
            if (method.ReturnType == typeof(void) && parameters.Length == 3 && parameters[0].ParameterType == savedPiton)
                return method;
        }
        return null;
    }

    // Runs inside the game's save loader, so it only reads one flag and never throws.
    private static bool ReplayPitonPrefix(ref Savegame.World.Piton piton)
    {
        try
        {
            if (!ShouldSkip(piton.savedPlayerPosition.isValid)) return true;

            _skipped++;
            if (_skipped == 1 || _skipped % 10 == 0)
                ModLog.Warning($"[LoadGuard] Skipped {_skipped} saved piton(s) with no climber pose.");
            return false;
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[LoadGuard] Could not check a saved piton: {ex.Message}");
            return true;
        }
    }
}
