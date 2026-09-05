using CairnMultiplayerMod.Internal.Diagnostics;
using System;
using HarmonyLib;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Captures a piton template for the rope team (system carried over from Episure). We patch
/// Piton.Awake to remember the first instantiated piton as the template to clone
/// (RopeInterop.UpdateRopeTeamAnchor). This is more reliable and cheaper than a
/// Resources.FindObjectsOfTypeAll every frame (which remains the fallback in RopeInterop.Belay).
///
/// The old approach (AddPiton + cosmetic rope + anti-respawn patch) has been replaced:
/// the lifeline's NATIVE rope, clipped onto a mobile piton placed at the partner, serves
/// both as the visual and as the belay (see RopeInterop.Belay). No more cosmetic rope, no more
/// repeated "clack", and fall arrest is handled natively.
/// </summary>
internal static unsafe class RopeTeamFallPatch
{
    private static readonly HarmonyLib.Harmony RopeTeamHarmony = new("CairnMultiplayerMod.RopeTeam");
    private static bool _ropeTeamPatchInstalled;
    private static bool _ropeTeamPatchFailed;

    public static void Install()
    {
        if (_ropeTeamPatchInstalled || _ropeTeamPatchFailed) return;

        try
        {
            var awake = AccessTools.Method(typeof(Piton), "Awake");
            var postfix = typeof(RopeTeamFallPatch).GetMethod(
                nameof(PitonAwakePostfix),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            if (awake == null || postfix == null)
            {
                _ropeTeamPatchFailed = true;
                ModLog.Warning("[RopeTeam] Piton.Awake / postfix not found — template capture via scan only.");
                return;
            }

            RopeTeamHarmony.Patch(awake, postfix: new HarmonyMethod(postfix));
            _ropeTeamPatchInstalled = true;
            ModLog.Debug("[RopeTeam] Piton template capture installed (Piton.Awake).");
        }
        catch (Exception ex)
        {
            _ropeTeamPatchFailed = true;
            ModLog.Warning($"[RopeTeam] template patch install failed: {ex.Message}");
        }
    }

    public static void Uninstall()
    {
        try { RopeTeamHarmony.UnpatchSelf(); }
        finally
        {
            _ropeTeamPatchInstalled = false;
            _ropeTeamPatchFailed = false;
        }
    }

    /// <summary>Remembers the first instantiated piton as the rope-team template.</summary>
    private static void PitonAwakePostfix(Piton __instance)
    {
        RopeInterop.CapturePitonTemplate(__instance);
    }
}
