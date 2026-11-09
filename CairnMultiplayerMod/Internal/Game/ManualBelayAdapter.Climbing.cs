using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using UnityEngine;
using NumericsVector3 = System.Numerics.Vector3;

namespace CairnMultiplayerMod.Internal.Game;

internal sealed partial class ManualBelayAdapter
{
    private bool _climbMovementBlocked;
    private double _nextClimbLimitLog;

    private static void AfterClimbingPermission(Harness __instance, Vector3 __0, ref bool __result)
    {
        var owner = _owner;
        if (owner?._ropeControl == null || !owner._ropeControl.Braked || owner._stopping
            || !__result || owner._ropeControl.Suspended) return;
        try
        {
            if (owner._lifeline == null || owner._lifeline.pawn == null
                || owner._lifeline.harness == null || Id(__instance) != Id(owner._lifeline.harness)
                || owner._lifeline.pawn.IsFalling || !ContextValid) return;
            if (!owner.MappingMatches(out var reason))
            {
                if (!owner.TryBeginLocalRouteChange(reason)) owner.EndLocalRope("Protection route changed during climbing permission. " + reason);
                return;
            }
            var radial = __instance.GetAttachPosition() - owner._lifeline.GetSecuringPitonPosition(true);
            if (!BelayClimbLimit.TryRestrict(__result, true, owner._ropeControl.Released,
                new NumericsVector3(radial.x, radial.y, radial.z), new NumericsVector3(__0.x, __0.y, __0.z), out var allowed))
            { owner.EndLocalRope("The climbing rope-limit geometry is invalid."); return; }
            if (owner._climbMovementBlocked != !allowed && Now >= owner._nextClimbLimitLog)
            {
                owner._nextClimbLimitLog = Now + .5;
                ModLog.Info("[BelayRope] Climbing blocked=" + !allowed + ", released=" + owner._ropeControl.Released
                    + ", distance=" + radial.magnitude);
            }
            owner._climbMovementBlocked = !allowed;
            __result = allowed;
        }
        catch (Exception ex) { owner.Fail(ex); }
    }
}
