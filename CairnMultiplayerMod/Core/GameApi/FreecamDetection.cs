using System;
using CairnMultiplayer.Shared;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Detection de la camera libre native de Cairn (bouton "Display Route" = vue
/// aerienne du trace / Eagle Eye).
///
/// L'etat est diffuse par les events statiques de `GameEventManager` :
///   OnEagleEye(bool)                       -> vue eagle eye generique
///   OnEagleEyePath(bool, RequestContext)   -> vue trace (Display Route)
/// Le bool indique l'entree (true) ou la sortie (false). On s'abonne aux deux
/// (plus les variantes "Request" pour diagnostic) et on maintient l'etat actif.
///
/// On garde aussi l'abonnement a `FreeCam.OnFreeCamActivate/Deactivate` pour la
/// free-cam de debug. "Freecam actif" = union de tous ces signaux.
///
/// Robustesse : si un abonnement echoue, on log une fois et on continue ; le ping
/// reste juste inactif, aucune exception ne remonte.
/// </summary>
public static unsafe partial class CairnGameApi
{
    private static bool _freecamHooksInstalled;
    private static bool _freecamHooksFailed;

    private static bool _freecamEventActive;   // FreeCam.OnFreeCamActivate/Deactivate
    private static bool _eagleEyeActive;        // GameEventManager.OnEagleEye
    private static bool _eagleEyePathActive;    // GameEventManager.OnEagleEyePath (Display Route)

    private static bool _hasLastReportedFreecam;
    private static bool _lastReportedFreecam;

    // Conserve les delegues IL2CPP pour empecher le GC de les collecter.
    private static Il2CppSystem.Action _freecamActivateDelegate;
    private static Il2CppSystem.Action _freecamDeactivateDelegate;
    private static Il2CppSystem.Action<bool> _eagleEyeDelegate;
    private static Il2CppSystem.Action<bool> _eagleEyeRequestDelegate;
    private static Il2CppSystem.Action<bool, Il2Cpp.CameraManager.EagleEyeRequestContext> _eagleEyePathDelegate;
    private static Il2CppSystem.Action<bool, Il2Cpp.CameraManager.EagleEyeRequestContext> _eagleEyePathRequestDelegate;

    /// <summary>
    /// Indique si une camera libre (Display Route / Eagle Eye, ou free-cam debug)
    /// est active. Etat pilote par les events natifs.
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
    /// Camera utilisee pour le raycast de ping. En vue libre, Camera.main est la
    /// camera active.
    /// </summary>
    public static bool TryGetFreecamCamera(out Camera cam)
    {
        cam = Camera.main;
        return cam != null;
    }

    /// <summary>
    /// Calcule le point monde a pinguer dans la direction de la camera (centre
    /// ecran) : raycast depuis la position camera vers l'avant. On ignore les
    /// colliders "trigger" pour viser la roche solide et pas les volumes de
    /// gameplay invisibles. Sans hit, on place a distance fixe
    /// (Protocol.DefaultPingDistance) devant la camera.
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
