using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Scopes direct-rope physics. The Lifeline is deliberately left alone: Cairn only counts
/// quickdraws on its securing rope as protection, and whatever hears "secured" then asks for
/// the last piton. A rope between two harnesses has none to give.
/// </summary>
internal static class RopeTeamFallPatch
{
    private static readonly HarmonyLib.Harmony Harmony = new("CairnMultiplayerMod.RopeTeam");
    private static bool _installed;
    internal static bool IsInstalled => _installed;

    public static void Install()
    {
        if (_installed) return;
        try
        {
            Harmony.Patch(AccessTools.Method(typeof(LogicalRope), nameof(LogicalRope.FixedUpdate)),
                prefix: new HarmonyMethod(typeof(RopeTeamFallPatch), nameof(BeforePhysics)));
            _installed = true;
        }
        catch (Exception ex)
        {
            Harmony.UnpatchSelf();
            ModLog.Warning("[RopeTeam] Direct-rope hooks unavailable: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        RopeInterop.ReleaseAllAnchors();
        Harmony.UnpatchSelf();
        _installed = false;
    }

    private static bool BeforePhysics(LogicalRope __instance) => RopeInterop.BeforeRopePhysics(__instance);
}
