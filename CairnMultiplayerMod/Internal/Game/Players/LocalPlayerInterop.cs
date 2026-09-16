using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Players;

internal static class LocalPlayerInterop
{
    private static bool _mcResolvedOnce;
    private static int _nullReadCount;

    internal static void ResetCaches()
    {
        _mcResolvedOnce = false;
        _nullReadCount = 0;
    }

    public static bool TryGetPose(out Vector3 position, out float yaw)
    {
        position = default;
        yaw = 0f;

        var go = TryGetMCGameObject();
        if (go == null)
        {
            // MC not yet present — still in a cutscene/loading. Log a heartbeat
            // every ~3 seconds so we know we're still polling.
            _nullReadCount++;
            if (_nullReadCount == 1 || _nullReadCount % 30 == 0)
                ModLog.Debug($"[LocalPlayer] MC still null (try #{_nullReadCount})");
            return false;
        }

        var t = go.transform;
        position = t.position;
        yaw = t.eulerAngles.y;

        // Log the first successful resolution — an important moment, we want to see it.
        if (!_mcResolvedOnce)
        {
            _mcResolvedOnce = true;
            ModLog.Debug($"[LocalPlayer] MC transform RESOLVED @ ({position.x:F1}, {position.y:F1}, {position.z:F1})  after {_nullReadCount} null reads");
        }
        return true;
    }

    /// <summary>Cloning the MC preserves native render setup that the netplay prefab lacks.</summary>
    public static GameObject TryGetMCGameObject()
    {
        try
        {
            return PawnManager.Instance?.MCGameObject;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("local-player.resolve-mc", exception);
            return null;
        }
    }
}
