using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>Scopes direct-rope physics, belay eligibility and personal piton operations.</summary>
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
            Harmony.Patch(AccessTools.Method(typeof(Lifeline), nameof(Lifeline.IsSecured)),
                postfix: new HarmonyMethod(typeof(RopeTeamFallPatch), nameof(AfterIsSecured)));
            foreach (var name in new[] { "AddPiton", "AttachToPiton", "DetachPiton", "RemovePiton" })
                foreach (var method in AccessTools.GetDeclaredMethods(typeof(Lifeline)))
                    if (method.Name == name)
                        Harmony.Patch(method,
                            prefix: new HarmonyMethod(typeof(RopeTeamFallPatch), nameof(BeforePersonalOperation)),
                            finalizer: new HarmonyMethod(typeof(RopeTeamFallPatch), nameof(AfterPersonalOperation)));
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

    private static void AfterIsSecured(Lifeline __instance, int minimumAnchorPoint, ref bool __result)
    {
        // A verified partner provides one anchor, not unlimited protection or extra pitons.
        if (!__result && minimumAnchorPoint == 1 && RopeInterop.ProvidesBelay(__instance)) __result = true;
    }
    private static void BeforePersonalOperation(Lifeline __instance, out bool __state)
        => __state = RopeInterop.BeginPersonalRopeOperation(__instance);

    private static Exception AfterPersonalOperation(Lifeline __instance, bool __state, Exception __exception)
    {
        try { if (__state) RopeInterop.EndPersonalRopeOperation(__instance); }
        catch (Exception ex)
        {
            ModLog.Warning("[RopeTeam] Could not restore rope after piton operation: " + ex.Message);
            RopeInterop.ReleaseAllAnchors();
            return __exception ?? ex;
        }
        return __exception;
    }
}
