using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Players;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.World;

/// <summary>
/// Long-distance teleport across zones. Cairn streams the world by ZONES. A plain far-away
/// `transform.position` drops the character into an unloaded zone -> fall into the void,
/// then streaming wildly loads ALL the zones crossed (origin + intermediate + target all stay
/// loaded) -> FPS drop + repositioning -> we never actually reach the player.
///
/// "Like the game does it" solution:
/// - Same zone (target in the current zone) -> direct INSTANT teleport (no loading).
/// - Different zone -> managed TRAVEL (CairnSceneManager.TravelToZone): loads the target zone AND
///   cleanly unloads the origin (loading screen), then we RE-APPLY the exact position
///   once the world is idle (settle), to land right on the player.
///
/// "idle" is detected via CairnSceneManager.IsLoadingOrUnloadingScenes + StreamingManager
/// .PreloadingZone (NOT IsZoneLoaded, which turns true too early).
/// </summary>
internal static unsafe class TeleportInterop
{
    private const float TeleportSettleTimeoutSeconds = 40f;   // global failsafe
    private const float TeleportStableDistance = 3f;          // "on target" tolerance
    private const float TeleportStableSeconds = 1.5f;         // idle+stable duration before releasing

    private static bool _teleportPending;
    private static Vector3 _teleportTarget;
    private static float _teleportYaw;
    private static float _teleportDeadline;
    private static float _teleportStableSince;   // -1 = not (yet) stable

    private static StreamingManager _streamingManagerCached;
    private static int _lastStreamingManagerSearchFrame;
    private static CairnSceneManager _sceneManagerCached;
    private static int _lastSceneManagerSearchFrame;

    /// <summary>
    /// Moves the local character (MC) to <paramref name="position"/> and orients its
    /// yaw. Used by the admin commands: /tp (the host moves locally toward a player)
    /// and /bring (a client receives a ServerTeleport and moves there).
    /// Returns false if the MC isn't instantiated yet (menu/cutscene).
    /// </summary>
    public static bool TeleportLocalPlayer(Vector3 position, float yawDeg)
    {
        var go = LocalPlayerInterop.TryGetMCGameObject();
        if (go == null) return false;

        // Target zone different from the current zone? -> we do as the game does: a managed TRAVEL
        // (load the target zone + clean unload of the origin), then set the exact position once
        // the world is idle. Otherwise (same zone), direct instant teleport.
        if (TryTeleportAcrossZones(position, yawDeg))
            return true;

        var t = go.transform;
        t.position = position;
        var euler = t.eulerAngles;
        t.eulerAngles = new Vector3(euler.x, yawDeg, euler.z);
        ModLog.Debug($"[Teleport] Teleported local MC to ({position.x:F1}, {position.y:F1}, {position.z:F1}) yaw={yawDeg:F0}");
        return true;
    }

    private static StreamingManager TryGetStreamingManager()
    {
        if (_streamingManagerCached != null) return _streamingManagerCached;
        if (_lastStreamingManagerSearchFrame != 0 && Time.frameCount - _lastStreamingManagerSearchFrame < 60) return null;
        _lastStreamingManagerSearchFrame = Time.frameCount;
        try { _streamingManagerCached = MoSingleton<StreamingManager>.Instance; }
        catch (Exception exception)
        {
            _streamingManagerCached = null;
            ModLog.SuppressedException("teleport.resolve-streaming-manager", exception);
        }
        return _streamingManagerCached;
    }

    private static CairnSceneManager TryGetSceneManager()
    {
        if (_sceneManagerCached != null) return _sceneManagerCached;
        if (_lastSceneManagerSearchFrame != 0 && Time.frameCount - _lastSceneManagerSearchFrame < 60) return null;
        _lastSceneManagerSearchFrame = Time.frameCount;
        try { _sceneManagerCached = MoSingleton<CairnSceneManager>.Instance; }
        catch (Exception exception)
        {
            _sceneManagerCached = null;
            ModLog.SuppressedException("teleport.resolve-scene-manager", exception);
        }
        return _sceneManagerCached;
    }

    /// <summary>
    /// If the target is in a DIFFERENT zone than the current one, starts a managed travel + arms the
    /// settle to apply the exact position afterwards, and returns true. Returns false if the target
    /// is in the current zone (or the info is unavailable) -> the caller does a direct instant teleport.
    /// </summary>
    private static bool TryTeleportAcrossZones(Vector3 pos, float yawDeg)
    {
        try
        {
            var sm = TryGetStreamingManager();
            var cm = TryGetSceneManager();
            if (sm == null || cm == null) return false;

            ZoneSceneData targetZone = null, currentZone = null;
            try { targetZone = sm.GetBestZoneAt(pos); }
            catch (Exception exception) { ModLog.SuppressedException("teleport.resolve-target-zone", exception); }
            try { currentZone = sm.CurrentZone; }
            catch (Exception exception) { ModLog.SuppressedException("teleport.resolve-current-zone", exception); }
            if (targetZone == null || currentZone == null) return false;

            // Same zone -> no loading, the caller will do a direct teleport.
            if (targetZone.Pointer == currentZone.Pointer) return false;

            var world = sm.World;
            if (world == null) return false;

            ModLog.Debug("[Teleport] Target in another zone -> travelling (loading)...");
            cm.TravelToZone(targetZone, world);

            // Apply the exact position once the world is idle (travel first places at the zone spawn).
            ArmTeleportSettle(pos, yawDeg);
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[Teleport] cross-zone travel failed ({ex.Message}) — falling back to direct teleport.");
            return false;
        }
    }

    /// <summary>Arms the settle: re-applies the target position until the world is idle.</summary>
    private static void ArmTeleportSettle(Vector3 pos, float yawDeg)
    {
        _teleportTarget = pos;
        _teleportYaw = yawDeg;
        _teleportPending = true;
        _teleportStableSince = -1f;
        _teleportDeadline = Time.time + TeleportSettleTimeoutSeconds;
        ModLog.Debug("[Teleport] Settling to exact target after zone load...");
    }

    /// <summary>True if the world is currently (un)loading scenes / preparing a zone.</summary>
    private static bool IsWorldStreamingBusy()
    {
        try
        {
            var sm = TryGetStreamingManager();
            if (sm != null)
            {
                try { if (sm.PreloadingZone != null) return true; }
                catch (Exception exception) { ModLog.SuppressedException("teleport.read-preloading-zone", exception); }
            }
            var cm = TryGetSceneManager();
            if (cm != null)
            {
                try { if (cm.IsLoadingOrUnloadingScenes) return true; }
                catch (Exception exception) { ModLog.SuppressedException("teleport.read-scene-loading", exception); }
                try { if (cm.IsTraveling) return true; }
                catch (Exception exception) { ModLog.SuppressedException("teleport.read-travel-state", exception); }
            }
        }
        catch (Exception exception) { ModLog.SuppressedException("teleport.detect-streaming", exception); }
        return false;
    }

    /// <summary>
    /// Call every frame from Mod.OnUpdate. For a cross-zone travel: once the world is idle,
    /// apply the exact position (travel first places at the zone spawn) and hold until stable.
    /// No-op if nothing is pending.
    /// </summary>
    public static void TickSettle(bool inGame)
    {
        if (!_teleportPending) return;
        try
        {
            if (Time.time >= _teleportDeadline)
            {
                _teleportPending = false;
                ModLog.Warning("[Teleport] Settle timed out. Released.");
                return;
            }

            // While the world is loading (or not in game), we wait — no messing with the position.
            if (!inGame || IsWorldStreamingBusy()) { _teleportStableSince = -1f; return; }

            var go = LocalPlayerInterop.TryGetMCGameObject();
            if (go == null) { _teleportStableSince = -1f; return; }

            var t = go.transform;
            float dist = Vector3.Distance(t.position, _teleportTarget);

            // World idle + in game: apply the exact position if travel put us somewhere else.
            if (dist > TeleportStableDistance)
            {
                t.position = _teleportTarget;
                var e = t.eulerAngles;
                t.eulerAngles = new Vector3(e.x, _teleportYaw, e.z);
                _teleportStableSince = -1f;
                return;
            }

            // On target, world idle, in game -> confirm stability before releasing.
            if (_teleportStableSince < 0f) _teleportStableSince = Time.time;
            else if (Time.time - _teleportStableSince >= TeleportStableSeconds)
            {
                _teleportPending = false;
                ModLog.Debug("[Teleport] Settled at target.");
            }
        }
        catch (Exception ex)
        {
            _teleportPending = false;
            _teleportStableSince = -1f;
            ModLog.Warning($"[Teleport] settle tick failed: {ex.Message}");
        }
    }
}
