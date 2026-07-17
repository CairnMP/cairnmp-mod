using System;
using CairnMultiplayer.Shared;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnMultiplayerMod.Features.PhotoMode;

/// <summary>
/// Detection of Cairn's native free camera (the "Display Route" button = aerial
/// route view / Eagle Eye).
///
/// The state is broadcast by `GameEventManager`'s static events:
///   OnEagleEye(bool)                       -> generic eagle eye view
///   OnEagleEyePath(bool, RequestContext)   -> route view (Display Route)
/// The bool indicates entry (true) or exit (false). We subscribe to both
/// (plus the "Request" variants for diagnostics) and keep the active state.
///
/// We also keep the subscription to `FreeCam.OnFreeCamActivate/Deactivate` for the
/// debug free-cam. "Freecam active" = union of all these signals.
///
/// Robustness: if a subscription fails, we log once and carry on; the ping just
/// stays inactive, no exception propagates.
/// </summary>
internal static unsafe class FreecamApi
{
    private static bool _freecamHooksInstalled;
    private static bool _freecamHooksFailed;

    private static bool _freecamEventActive;   // FreeCam.OnFreeCamActivate/Deactivate
    private static bool _eagleEyeActive;        // GameEventManager.OnEagleEye
    private static bool _eagleEyePathActive;    // GameEventManager.OnEagleEyePath (Display Route)

    private static bool _hasLastReportedFreecam;
    private static bool _lastReportedFreecam;

    // Keep the IL2CPP delegates to prevent the GC from collecting them.
    private static Il2CppSystem.Action _freecamActivateDelegate;
    private static Il2CppSystem.Action _freecamDeactivateDelegate;
    private static Il2CppSystem.Action<bool> _eagleEyeDelegate;
    private static Il2CppSystem.Action<bool> _eagleEyeRequestDelegate;
    private static Il2CppSystem.Action<bool, Il2Cpp.CameraManager.EagleEyeRequestContext> _eagleEyePathDelegate;
    private static Il2CppSystem.Action<bool, Il2Cpp.CameraManager.EagleEyeRequestContext> _eagleEyePathRequestDelegate;

    /// <summary>
    /// Indicates whether a free camera (Display Route / Eagle Eye, or debug free-cam)
    /// is active. State driven by the native events.
    /// </summary>
    public static bool TryIsFreecamActive(out bool active)
    {
        EnsureFreecamHooks();

        active = _eagleEyePathActive || _eagleEyeActive || _freecamEventActive;

        if (!_hasLastReportedFreecam || _lastReportedFreecam != active)
        {
            _hasLastReportedFreecam = true;
            _lastReportedFreecam = active;
            Mod.LogDebug($"[Freecam] active -> {active} (path={_eagleEyePathActive}, eagle={_eagleEyeActive}, freeCam={_freecamEventActive})");
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
            Mod.LogDebug("[Freecam] Subscribed to GameEventManager EagleEye/FreeCam events");
        }
        catch (Exception ex)
        {
            _freecamHooksFailed = true;
            Mod.Log.Warning($"[Freecam] Hook install failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void OnNativeFreeCamActivate() => _freecamEventActive = true;
    private static void OnNativeFreeCamDeactivate() => _freecamEventActive = false;

    private static void OnNativeEagleEye(bool on)
    {
        _eagleEyeActive = on;
        Mod.LogDebug($"[Freecam] OnEagleEye({on})");
    }

    private static void OnNativeEagleEyeRequest(bool on)
        => Mod.LogDebug($"[Freecam] OnRequestEagleEye({on})");

    private static void OnNativeEagleEyePath(bool on, Il2Cpp.CameraManager.EagleEyeRequestContext ctx)
    {
        _eagleEyePathActive = on;
        Mod.LogDebug($"[Freecam] OnEagleEyePath({on}, {ctx})");
    }

    private static void OnNativeEagleEyePathRequest(bool on, Il2Cpp.CameraManager.EagleEyeRequestContext ctx)
        => Mod.LogDebug($"[Freecam] OnRequestEagleEyePath({on}, {ctx})");

    /// <summary>
    /// Camera used for the ping raycast. In free view, Camera.main is the active
    /// camera.
    /// </summary>
    public static bool TryGetFreecamCamera(out Camera cam)
    {
        cam = Camera.main;
        return cam != null;
    }

    /// <summary>
    /// Computes the world point to ping in the camera's direction (screen center):
    /// a raycast from the camera position forward. We ignore "trigger" colliders to
    /// aim at solid rock and not the invisible gameplay volumes. With no hit, we
    /// place it at a fixed distance (Protocol.DefaultPingDistance) in front of the
    /// camera.
    /// </summary>
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
        catch
        {
            point = origin + forward * Protocol.DefaultPingDistance;
        }

        return true;
    }
}
