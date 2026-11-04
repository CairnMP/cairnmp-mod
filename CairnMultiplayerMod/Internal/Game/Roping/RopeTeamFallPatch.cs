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
            var fixedUpdate = AccessTools.Method(typeof(LogicalRope), nameof(LogicalRope.FixedUpdate));
            Harmony.Patch(fixedUpdate,
                prefix: new HarmonyMethod(typeof(RopeTeamFallPatch), nameof(BeforePhysics)),
                postfix: new HarmonyMethod(typeof(RopeTeamFallPatch), nameof(AfterPhysics)));
            // The rope writes its particle line from LateUpdate. That line is the
            // straight one leaving the harnesses, so the replacement is drawn after it.
            var lateUpdate = AccessTools.Method(typeof(LogicalRopeRenderer), nameof(LogicalRopeRenderer.LateUpdate));
            Harmony.Patch(lateUpdate, postfix: new HarmonyMethod(typeof(RopeTeamFallPatch), nameof(AfterRender)));
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

    private static void AfterPhysics(LogicalRope __instance) => RopeInterop.AfterRopePhysics(__instance);

    private static void AfterRender(LogicalRopeRenderer __instance) => RopeInterop.AfterRopeRendered(__instance);
}
