using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.World;

/// <summary>
/// Display Route, Eagle Eye and debug freecam emit separate native events, so their union defines
/// whether camera-aimed actions are available.
/// </summary>
internal static unsafe class FreecamInterop
{
    private static bool _freecamHooksInstalled;
    private static bool _freecamHooksFailed;

    private static bool _freecamEventActive;
    private static bool _eagleEyeActive;
    private static bool _eagleEyePathActive;

    private static bool _hasLastReportedFreecam;
    private static bool _lastReportedFreecam;

    // Keep the IL2CPP delegates to prevent the GC from collecting them.
    private static Il2CppSystem.Action _freecamActivateDelegate;
    private static Il2CppSystem.Action _freecamDeactivateDelegate;
    private static Il2CppSystem.Action<bool> _eagleEyeDelegate;
    private static Il2CppSystem.Action<bool> _eagleEyeRequestDelegate;
    private static Il2CppSystem.Action<bool, Il2Cpp.CameraManager.EagleEyeRequestContext> _eagleEyePathDelegate;
    private static Il2CppSystem.Action<bool, Il2Cpp.CameraManager.EagleEyeRequestContext> _eagleEyePathRequestDelegate;

    public static bool TryIsActive(out bool active)
    {
        EnsureFreecamHooks();

        active = _eagleEyePathActive || _eagleEyeActive || _freecamEventActive;

        if (!_hasLastReportedFreecam || _lastReportedFreecam != active)
        {
            _hasLastReportedFreecam = true;
            _lastReportedFreecam = active;
            ModLog.Debug($"[Freecam] active -> {active} (path={_eagleEyePathActive}, eagle={_eagleEyeActive}, freeCam={_freecamEventActive})");
        }

        return _freecamHooksInstalled;
    }

    private static void EnsureFreecamHooks()
    {
        if (_freecamHooksInstalled || _freecamHooksFailed)
            return;

        try
        {
            _freecamActivateDelegate = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
                (Action)OnNativeFreeCamActivate);
            _freecamDeactivateDelegate = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
                (Action)OnNativeFreeCamDeactivate);
            Il2Cpp.GameEventManager.FreeCam.OnFreeCamActivate += _freecamActivateDelegate;
            Il2Cpp.GameEventManager.FreeCam.OnFreeCamDeactivate += _freecamDeactivateDelegate;

            _eagleEyeDelegate = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(
                (Action<bool>)OnNativeEagleEye);
            _eagleEyeRequestDelegate = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(
                (Action<bool>)OnNativeEagleEyeRequest);
            Il2Cpp.GameEventManager.OnEagleEye += _eagleEyeDelegate;
            Il2Cpp.GameEventManager.OnRequestEagleEye += _eagleEyeRequestDelegate;

            _eagleEyePathDelegate = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool, Il2Cpp.CameraManager.EagleEyeRequestContext>>(
                (Action<bool, Il2Cpp.CameraManager.EagleEyeRequestContext>)OnNativeEagleEyePath);
            _eagleEyePathRequestDelegate = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool, Il2Cpp.CameraManager.EagleEyeRequestContext>>(
                (Action<bool, Il2Cpp.CameraManager.EagleEyeRequestContext>)OnNativeEagleEyePathRequest);
            Il2Cpp.GameEventManager.OnEagleEyePath += _eagleEyePathDelegate;
            Il2Cpp.GameEventManager.OnRequestEagleEyePath += _eagleEyePathRequestDelegate;

            _freecamHooksInstalled = true;
            ModLog.Debug("[Freecam] Subscribed to GameEventManager EagleEye/FreeCam events");
        }
        catch (Exception ex)
        {
            _freecamHooksFailed = true;
            ModLog.Warning($"[Freecam] Hook install failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void OnNativeFreeCamActivate() => _freecamEventActive = true;
    private static void OnNativeFreeCamDeactivate() => _freecamEventActive = false;

    private static void OnNativeEagleEye(bool on)
    {
        _eagleEyeActive = on;
        ModLog.Debug($"[Freecam] OnEagleEye({on})");
    }

    private static void OnNativeEagleEyeRequest(bool on)
        => ModLog.Debug($"[Freecam] OnRequestEagleEye({on})");

    private static void OnNativeEagleEyePath(bool on, Il2Cpp.CameraManager.EagleEyeRequestContext ctx)
    {
        _eagleEyePathActive = on;
        ModLog.Debug($"[Freecam] OnEagleEyePath({on}, {ctx})");
    }

    private static void OnNativeEagleEyePathRequest(bool on, Il2Cpp.CameraManager.EagleEyeRequestContext ctx)
        => ModLog.Debug($"[Freecam] OnRequestEagleEyePath({on}, {ctx})");

    public static bool TryGetFreecamCamera(out Camera cam)
    {
        cam = Camera.main;
        return cam != null;
    }

    /// <summary>Trigger colliders are ignored because invisible gameplay volumes are not useful targets.</summary>
    public static bool TryComputePingPoint(out Vector3 point)
    {
        point = default;
        if (!TryGetFreecamCamera(out var cam) || cam == null)
            return false;

        var t = cam.transform;
        var origin = t.position;
        var forward = t.forward;

        try
        {
            const float maxDistance = 10000f;
            if (Physics.Raycast(origin, forward, out var hit, maxDistance,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                point = hit.point;
            else
                point = origin + forward * Protocol.DefaultPingDistance;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("freecam.raycast-ping-position", exception);
            point = origin + forward * Protocol.DefaultPingDistance;
        }

        return true;
    }
}
