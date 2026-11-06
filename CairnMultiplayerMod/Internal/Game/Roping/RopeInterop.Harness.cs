using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Access to the rope's attach point on the harness (Harness). The game exposes
/// `Harness.GetAttachPosition()`: the exact world position where the rope ties to
/// the harness. We use it to anchor the rope between players as Cairn does, instead
/// of an approximate vertical offset on the body root.
/// </summary>
internal static partial class RopeInterop
{
    private static Harness _localHarnessCached;
    private static int _lastLocalHarnessSearchFrame;

    /// <summary>Returns the local player's Harness from Cairn's PawnManager. The MC may
    /// appear late, so failed reads are throttled.</summary>
    private static Harness ResolveLocalHarness()
    {
        if (_localHarnessCached != null) return _localHarnessCached;

        if (_lastLocalHarnessSearchFrame != 0 && Time.frameCount - _lastLocalHarnessSearchFrame < 30)
            return null;
        _lastLocalHarnessSearchFrame = Time.frameCount;

        try
        {
            // Cairn stores the authoritative local harness on its climbing controller.
            // This avoids walking the complete MC hierarchy and cannot select a remote rig.
            var harness = PawnManager.Instance?.ClimbingPawnController?.harness;
            if (harness == null) return null;
            _localHarnessCached = harness;
            ModLog.Debug("[Harness] Local harness resolved through PawnManager");
            return harness;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.resolve-local-harness", exception);
            return null;
        }
    }

    /// <summary>
    /// World position of any harness's attach point (local or ghost). Falls back to
    /// `skeletonAttachPointRoot` if GetAttachPosition returns the origin (harness not
    /// yet initialized / physics inactive on the ghost side).
    /// </summary>
    public static bool TryGetHarnessAttachPosition(Harness harness, out Vector3 pos)
    {
        pos = default;
        if (harness == null) return false;
        try
        {
            pos = harness.GetAttachPosition();
            if (Usable(pos)) return true;

            // Fallback: the skeleton attach root follows the pose even without physics.
            var skeleton = harness.skeletonAttachPointRoot;
            if (skeleton != null && Usable(skeleton.position))
            {
                pos = skeleton.position;
                return true;
            }

            // A bind-pose practice climber can report both of those at the origin.
            // The harness transform still follows the ghost root we move.
            var body = harness.transform;
            if (body != null && Usable(body.position))
            {
                pos = body.position;
                return true;
            }

            return false;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.resolve-harness", exception);
            return false;
        }

        static bool Usable(Vector3 point)
            => float.IsFinite(point.x) && float.IsFinite(point.y) && float.IsFinite(point.z) && point != Vector3.zero;
    }
}
