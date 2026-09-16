using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Players;

/// <summary>
/// Syncs the lamp (AavaLightStick) between players.
///
/// The vanilla API is NOT a bool on/off but a Mode enum driven by
/// SetMode(Mode, bool instant). We carry the Mode as an int in the packet
/// and apply it as-is on the remote ghost via SetMode.
/// </summary>
internal static class LampInterop
{
    private static AavaLightStick _localLightStickCached;
    private static int _lastLocalLightStickSearchFrame;
    private static bool _localLightStickWarningLogged;

    public static bool TryGetLocalState(out int mode)
    {
        mode = 0;
        var lamp = TryGetLocalLightStick();
        if (lamp == null) return false;

        try
        {
            mode = (int)lamp.CurrentMode;
            return true;
        }
        catch (Exception ex)
        {
            if (!_localLightStickWarningLogged)
            {
                _localLightStickWarningLogged = true;
                ModLog.Warning($"[LampSync] CurrentMode read failed: {ex.GetType().Name}:{ex.Message}");
            }
        }
        return false;
    }

    public static bool TryApplyRemoteState(NetplayRemotePlayer remote, int mode)
    {
        if (remote == null || remote.Pointer == IntPtr.Zero) return false;

        AavaLightStick lamp;
        try
        {
            lamp = remote.LightStick;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("lamp.capture-local-state", exception);
            return false;
        }

        if (lamp == null) return false;

        try
        {
            if ((int)lamp.CurrentMode == mode) return true;

            lamp.SetMode((AavaLightStick.Mode)mode, forceUpdate: true);
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[LampSync] Apply failed: {ex.GetType().Name}:{ex.Message}");
            return false;
        }
    }

    public static void ResetCaches()
    {
        _localLightStickCached = null;
        _lastLocalLightStickSearchFrame = 0;
        _localLightStickWarningLogged = false;
    }

    private static AavaLightStick TryGetLocalLightStick()
    {
        if (_localLightStickCached != null && _localLightStickCached.Pointer != IntPtr.Zero)
            return _localLightStickCached;

        var frame = Time.frameCount;
        if (frame == _lastLocalLightStickSearchFrame)
            return null;
        _lastLocalLightStickSearchFrame = frame;

        var mc = LocalPlayerInterop.TryGetMCGameObject();
        if (mc == null) return null;

        try
        {
            var lamp = mc.GetComponentInChildren<AavaLightStick>(true);
            if (lamp != null)
            {
                _localLightStickCached = lamp;
                return lamp;
            }
        }
        catch (Exception ex)
        {
            if (!_localLightStickWarningLogged)
            {
                _localLightStickWarningLogged = true;
                ModLog.Warning($"[LampSync] Local lookup failed: {ex.GetType().Name}:{ex.Message}");
            }
        }

        return null;
    }
}
