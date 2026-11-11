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
    private static readonly Type[] RopeHangModules =
    {
        typeof(ClimbingV2PawnControllerModuleRappelAscendRopeHangIdle),
        typeof(ClimbingV2PawnControllerModuleAbseil),
        typeof(ClimbingV2PawnControllerModuleAbseiling),
        typeof(ClimbingV2PawnControllerModuleRappel),
        typeof(ClimbingV2PawnControllerModuleRopeAscending),
    };

    private static bool _installed;
    private static int _suppressStop;
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
            // Leaving a rope hang calls OnModuleStop, which null-references once the
            // module has no Cairn rope. The untie skips that stop and starts the fall.
            var skip = new HarmonyMethod(typeof(RopeTeamFallPatch), nameof(SkipBrokenStop));
            foreach (var module in RopeHangModules)
            {
                var stop = AccessTools.Method(module, "OnModuleStop");
                if (stop == null) continue;
                Harmony.Patch(stop, prefix: skip);
            }

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

    /// <summary>
    /// Drop a climber who is dangling on the rope. OnModuleStop is skipped for this call.
    /// The falling module has to be entered by Cairn's own call, which records the reason.
    /// Assigning it directly is an invalid fall and leaves the climber standing.
    /// </summary>
    internal static void FallOffRope(ClimbingV2PawnController pawn)
    {
        _suppressStop++;
        try
        {
            RopeInterop.StartUnsecuredFall(pawn, FallReason.Voluntary);
        }
        finally
        {
            _suppressStop--;
        }
    }

    private static bool SkipBrokenStop() => _suppressStop == 0;

    private static bool BeforePhysics(LogicalRope __instance) => RopeInterop.BeforeRopePhysics(__instance);

    private static void AfterPhysics(LogicalRope __instance) => RopeInterop.AfterRopePhysics(__instance);

    private static void AfterRender(LogicalRopeRenderer __instance) => RopeInterop.AfterRopeRendered(__instance);
}
