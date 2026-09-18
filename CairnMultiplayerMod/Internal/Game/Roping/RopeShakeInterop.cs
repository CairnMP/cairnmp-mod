using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// What a partner's fall does to the climbers tied to them.
///
/// This is Cairn's own rule, read out of its shared-rope protocol: every limb that is merely
/// holding lets go, and the game even has a drop cause named <c>SharedRope</c> for it. A
/// climber holding firmly rides it out -- which is the whole tension of being roped up.
/// </summary>
internal static class RopeShakeInterop
{
    internal static bool ShakeLocalClimber()
    {
        try
        {
            var climber = PawnManager.Instance?.ClimbingPawnController;
            if (climber == null) return false;

            // A firm hold resists the rope entirely, exactly as the native protocol has it.
            if (climber.IsHoldingFirmly()) return false;

            // The backing array rather than the IReadOnlyList property: the interop wrapper
            // for that interface exposes neither Count nor an enumerator.
            var limbs = climber.limbs;
            if (limbs == null) return false;

            var shaken = 0;
            for (var index = 0; index < limbs.Length; index++)
            {
                var limb = limbs[index];
                if (limb == null || !limb.IsHolding) continue;
                limb.Drop(ClimbingV2PawnLimb.DropCause.SharedRope, UnityEngine.Vector3.zero);
                shaken++;
            }

            if (shaken > 0) ModLog.Debug($"[RopeShake] {shaken} limb(s) shaken off their holds");
            return shaken > 0;
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[RopeShake] Could not shake the local climber: {exception.Message}");
            return false;
        }
    }
}
