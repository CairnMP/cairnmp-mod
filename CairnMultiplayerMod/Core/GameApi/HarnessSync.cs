using System;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Access to the rope's attach point on the harness (Harness). The game exposes
/// `Harness.GetAttachPosition()`: the exact world position where the rope ties to
/// the harness. We use it to anchor the rope between players as Cairn does, instead
/// of an approximate vertical offset on the body root.
/// </summary>
public static unsafe partial class CairnGameApi
{
    private static Harness _localHarnessCached;
    private static int _lastLocalHarnessSearchFrame;

    /// <summary>
    /// World position of the local player's harness attach point, or false if the
    /// harness isn't (yet) available.
    /// </summary>
    public static bool TryGetLocalHarnessAttachPosition(out Vector3 pos)
    {
        pos = default;
        var harness = ResolveLocalHarness();
        return harness != null && TryGetHarnessAttachPosition(harness, out pos);
    }

    /// <summary>Returns the local player's Harness component (for the belay probe / physical rope).</summary>
    public static bool TryGetLocalHarness(out Harness harness)
    {
        harness = ResolveLocalHarness();
        return harness != null;
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
            if (pos != Vector3.zero) return true;

            // Fallback: the skeleton attach root follows the pose even without physics.
            var root = harness.skeletonAttachPointRoot;
            if (root != null)
            {
                pos = root.position;
                return pos != Vector3.zero;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static Harness ResolveLocalHarness()
    {
        if (_localHarnessCached != null) return _localHarnessCached;

        // Throttled search while nothing is found (the MC may appear late).
        if (_lastLocalHarnessSearchFrame != 0 && Time.frameCount - _lastLocalHarnessSearchFrame < 30)
            return null;
        _lastLocalHarnessSearchFrame = Time.frameCount;

        var mc = TryGetLocalMCGameObject();
        if (mc == null) return null;

        // The local harness is a Harness in the MC's hierarchy — but NOT a
        // NetplayRemoteHarness (those belong to the ghosts).
        var harnesses = mc.GetComponentsInChildren<Harness>(true);
        if (harnesses == null) return null;
        for (int i = 0; i < harnesses.Length; i++)
        {
            var h = harnesses[i];
            if (h == null) continue;
            if (h.TryCast<Il2CppTheGameBakers.Cairn.Netplay.NetplayRemoteHarness>() != null) continue;
            _localHarnessCached = h;
            Mod.LogDebug("[Harness] Local harness resolved on MC hierarchy");
            return h;
        }
        return null;
    }
}
