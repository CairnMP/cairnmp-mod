using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Players;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.World;

/// <summary>
/// Cross-zone transforms fall into unloaded terrain, so distant teleports use Cairn's managed
/// travel and reapply the exact target only after streaming becomes idle.
/// </summary>
internal static unsafe class TeleportInterop
{
    private const float TeleportSettleTimeoutSeconds = 40f;
    private const float TeleportStableDistance = 3f;
    private const float TeleportStableSeconds = 1.5f;

    private static bool _teleportPending;
    private static Vector3 _teleportTarget;
    private static float _teleportYaw;
    private static float _teleportDeadline;
    private static float _teleportStableSince;

    private static StreamingManager _streamingManagerCached;
    private static int _lastStreamingManagerSearchFrame;
    private static CairnSceneManager _sceneManagerCached;
    private static int _lastSceneManagerSearchFrame;

    /// <summary>
    /// Whether the local pawn can be moved right now. Cairn only tolerates a hard transform
    /// write while the pawn is in a plain grounded walk: climbing keeps live references to
    /// holds and rope constraints, and falling/dead run their own recovery. Moving the pawn
    /// out from under either leaves the native controller pointing at geometry that is no
    /// longer there.
    /// </summary>
    public static bool CanTeleportLocalPlayer(out string reason)
    {
        reason = null;

        if (LocalPlayerInterop.TryGetMCGameObject() == null)
        {
            reason = "you are not in game";
            return false;
        }

        var state = PawnCaptureInterop.GetLocalPawnState().ToString();
        if (TeleportPolicy.AllowsTeleport(state)) return true;

        reason = TeleportPolicy.DescribeRefusal(state);
        return false;
    }

    public static bool TeleportLocalPlayer(Vector3 position, float yawDeg)
        => TeleportLocalPlayer(position, yawDeg, out _);

    public static bool TeleportLocalPlayer(Vector3 position, float yawDeg, out string refusedReason)
    {
        // The same guard covers both entry points: /tp moving the host, and a ServerTeleport
        // moving whoever the host brings over. Only the pawn being moved matters here.
        if (!CanTeleportLocalPlayer(out refusedReason))
        {
            ModLog.Debug($"[Teleport] Refused: {refusedReason}");
            return false;
        }

        var go = LocalPlayerInterop.TryGetMCGameObject();
        if (go == null)
        {
            refusedReason = "you are not in game";
            return false;
        }

        Roping.RopeInterop.ReleaseAllAnchors();

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

            if (targetZone.Pointer == currentZone.Pointer) return false;

            var world = sm.World;
            if (world == null) return false;

            ModLog.Debug("[Teleport] Target in another zone -> travelling (loading)...");
            cm.TravelToZone(targetZone, world);

            ArmTeleportSettle(pos, yawDeg);
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[Teleport] cross-zone travel failed ({ex.Message}) — falling back to direct teleport.");
            return false;
        }
    }

    private static void ArmTeleportSettle(Vector3 pos, float yawDeg)
    {
        _teleportTarget = pos;
        _teleportYaw = yawDeg;
        _teleportPending = true;
        _teleportStableSince = -1f;
        _teleportDeadline = Time.time + TeleportSettleTimeoutSeconds;
        ModLog.Debug("[Teleport] Settling to exact target after zone load...");
    }

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

    /// <summary>Managed travel lands at the zone spawn, so the exact target must be restored later.</summary>
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

            if (!inGame || IsWorldStreamingBusy()) { _teleportStableSince = -1f; return; }

            // Zone travel drops the pawn at the zone spawn, and it may still be falling or
            // grabbing a wall. Repositioning it then is the same hazard as the initial
            // teleport, so wait for a grounded walk exactly as we wait for streaming. The
            // 40 s deadline still releases us if that never happens.
            if (!PawnCaptureInterop.IsLocalPlayerWalking()) { _teleportStableSince = -1f; return; }

            var go = LocalPlayerInterop.TryGetMCGameObject();
            if (go == null) { _teleportStableSince = -1f; return; }

            var t = go.transform;
            float dist = Vector3.Distance(t.position, _teleportTarget);

            if (dist > TeleportStableDistance)
            {
                t.position = _teleportTarget;
                var e = t.eulerAngles;
                t.eulerAngles = new Vector3(e.x, _teleportYaw, e.z);
                _teleportStableSince = -1f;
                return;
            }

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
