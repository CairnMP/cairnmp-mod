using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Game.Roping;
using CairnMultiplayerMod.Internal.Networking;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Players;

/// <summary>
/// Handles periodic broadcasting of the local player state, the local lifecycle-state
/// machine, piton checks and the ghost lifecycle. Ticked every frame from OnUpdate().
/// </summary>
internal sealed class PlayerStateBroadcaster
{
    private readonly NetworkManager _network;
    private readonly RuntimeState _state;
    private readonly System.Action _resetRope;
    private readonly System.Func<bool> _isGameplaySyncSuspended;

    private float _stateTickTimer;
    private float _boneTickTimer;

    private const float NetFrameMissingLogIntervalSeconds = 3f;
    private bool _debugLoggedFirstPlayerFrameCapture;
    private bool _debugLoggedFirstClimbotFrameCapture;
    private float _debugLastMissingPlayerFrameLogAt;
    private float _debugLastMissingClimbotFrameLogAt;

    internal PlayerStateBroadcaster(
        NetworkManager network,
        RuntimeState state,
        System.Action resetRope,
        System.Func<bool> isGameplaySyncSuspended)
    {
        _network = network;
        _state = state ?? throw new System.ArgumentNullException(nameof(state));
        _resetRope = resetRope ?? throw new System.ArgumentNullException(nameof(resetRope));
        _isGameplaySyncSuspended = isGameplaySyncSuspended
            ?? throw new System.ArgumentNullException(nameof(isGameplaySyncSuspended));
    }

    /// <summary>Resets the state/bone broadcast cadence timers (on a scene reload / bivouac
    /// boundary).</summary>
    internal void ResetTimers()
    {
        _stateTickTimer = 0f;
        _boneTickTimer = 0f;
    }

    /// <summary>
    /// Periodic broadcast of the local player state + sync of remote ghosts.
    /// </summary>
    internal void Tick()
    {
        // Periodic broadcast of the local player state + sync of remote ghosts.
        if (_network.IsHandshakeComplete)
        {
            if (_isGameplaySyncSuspended())
            {
                TickSuspendedNetworkPresence();
                return;
            }

            _stateTickTimer += Time.unscaledDeltaTime;
            if (_stateTickTimer >= Protocol.PlayerStateUpdateIntervalSeconds)
            {
                _stateTickTimer = 0f;
                SendLocalPlayerState();
            }

            _boneTickTimer += Time.unscaledDeltaTime;
            if (_boneTickTimer >= Protocol.BoneStateUpdateIntervalSeconds)
            {
                _boneTickTimer = 0f;
                SendLocalNetFrames();
            }


            // Check for newly placed pitons (a lower frequency is enough).
            if (_state.LocalPlayerState == PlayerState.InGame)
            {
                if (RopeInterop.CheckForNewPiton(out var pitonId, out var pitonPos,
                    out var pitonRot, out var pitonQuality, out var pitonHp, out var pitonItemId))
                {
                    _network.SendPitonPlaced(new ClientPitonPlaced
                    {
                        PitonId = pitonId,
                        PosX = pitonPos.x,
                        PosY = pitonPos.y,
                        PosZ = pitonPos.z,
                        RotX = pitonRot.x,
                        RotY = pitonRot.y,
                        RotZ = pitonRot.z,
                        RotW = pitonRot.w,
                        Quality = pitonQuality,
                        PitonHp = pitonHp,
                        ItemId = pitonItemId,
                    });
                }

                if (RopeInterop.CheckForRemovedPiton(out var removedPitonId))
                {
                    _network.SendPitonRemoved(removedPitonId);
                }
            }

            // Ghost lifecycle: spawn early, keep them alive across transitions,
            // then drive the transforms from the latest packets.
            RemotePlayerManager.Reconcile(_network, _state.LocalPlayerState);
            RemotePlayerManager.UpdateAll(_network, _state.LocalPlayerState);
        }
        else
        {
            // Disconnected — remove any leftover ghosts.
            if (_state.LocalPlayerState == PlayerState.Unknown)
                RemotePlayerManager.ClearAll();
        }
    }

    /// <summary>
    /// Keeps a minimal network presence during the bivouac without touching the
    /// original gameplay graph (no MC, NetFrame, weather, pitons, ghosts).
    /// </summary>
    internal void TickSuspendedNetworkPresence()
    {
        _boneTickTimer = 0f;

        _stateTickTimer += Time.unscaledDeltaTime;
        if (_stateTickTimer < Protocol.PlayerStateUpdateIntervalSeconds)
            return;

        _stateTickTimer = 0f;
        _network.SendPlayerState(0f, 0f, 0f, 0f, CurrentNetworkSceneName(), PlayerState.Bivouac);
    }

    /// <summary>
    /// Sends the local player's body position + the current lifecycle state.
    /// Prefers the real MC transform via PawnManager.MCGameObject; falls back to
    /// Camera.main until the MC has been instantiated.
    /// </summary>
    private void SendLocalPlayerState()
    {
        if (_state.LocalPlayerState != PlayerState.InGame)
        {
            _network.SendPlayerState(0f, 0f, 0f, 0f, CurrentNetworkSceneName(), _state.LocalPlayerState);
            return;
        }

        Vector3 p; float yaw;
        if (!LocalPlayerInterop.TryGetPose(out p, out yaw))
        {
            var cam = Camera.main;
            if (cam == null) return;
            var t = cam.transform;
            p = t.position;
            yaw = t.eulerAngles.y;
        }
        _network.SendPlayerState(p.x, p.y, p.z, yaw, CurrentNetworkSceneName(), _state.LocalPlayerState);
    }

    /// <summary>
    /// Determines the current lifecycle state from the available signals
    /// (connection, handshake, scene, MC appearance, scene stability).
    /// </summary>
    /// <remarks>
    /// The `InGame` state is gated on ~1 second of scene stability after the MC is
    /// resolved. This is the critical rule: we never spawn or despawn ghosts during
    /// scene transitions, because that's when Cairn's Addressables pipeline crashes
    /// when we touch NetplayClimberPrefab instances.
    /// </remarks>
    internal PlayerState ComputeLocalState()
    {
        if (!_network.IsConnected)
            return PlayerState.Unknown;
        if (!_network.IsHandshakeComplete)
            return PlayerState.Connecting;
        var currentScene = _state.CurrentScene;
        if (currentScene == null)
            return PlayerState.Connecting;

        if (SceneRoles.IsMainMenuArea(currentScene))
            return PlayerState.InMenu;

        if (SceneRoles.IsLoading(currentScene))
            return PlayerState.Loading;

        if (GameLifecycleService.TryGetGameLifecycle(out var lifecycle, out _))
        {
            if (lifecycle == CairnGameLifecycleState.Menu)
                return PlayerState.InMenu;

            if (lifecycle != CairnGameLifecycleState.InGame)
                return PlayerState.Loading;
        }
        else if (SceneRoles.IsBivouac(currentScene))
        {
            return PlayerState.Loading;
        }

        // In a gameplay scene, wait for the graph to be stable before allowing
        // native captures and ghosts. After a death, a camera can be active before
        // the new MC exists; using that camera as an InGame signal would trigger
        // captures on destroyed objects.
        if (_state.TimeSinceLastSceneLoad < 1.0f)
            return PlayerState.Loading;

        if (LocalPlayerInterop.TryGetPose(out _, out _))
            return PlayerState.InGame;

        return PlayerState.Loading;
    }

    /// <summary>
    /// Captures and sends the local native Netplay frames at animation rate.
    /// </summary>
    private void SendLocalNetFrames()
    {
        if (_state.LocalPlayerState != PlayerState.InGame) return;

        if (PawnCaptureInterop.TryCaptureLocalPlayerFrame(out var playerFrame))
        {
            if (!_debugLoggedFirstPlayerFrameCapture)
            {
                _debugLoggedFirstPlayerFrameCapture = true;
                ModLog.Debug($"[NetSync] First local player NetFrame captured positions={FrameVectorCount(playerFrame.Positions)} eulers={FrameVectorCount(playerFrame.Eulers)} flags=0x{playerFrame.Flags:X2}");
            }
            _network.SendPlayerFrame(playerFrame);
        }
        else
        {
            LogMissingLocalNetFrame("player", ref _debugLastMissingPlayerFrameLogAt);
        }

        if (PawnCaptureInterop.TryCaptureLocalClimbotFrame(out var climbotFrame))
        {
            if (!_debugLoggedFirstClimbotFrameCapture)
            {
                _debugLoggedFirstClimbotFrameCapture = true;
                ModLog.Debug($"[NetSync] First local climbot NetFrame captured positions={FrameVectorCount(climbotFrame.Positions)} eulers={FrameVectorCount(climbotFrame.Eulers)} flags=0x{climbotFrame.Flags:X2}");
            }
            _network.SendClimbotFrame(climbotFrame);
        }
        else
        {
            LogMissingLocalNetFrame("climbot", ref _debugLastMissingClimbotFrameLogAt);
        }
    }

    private static int FrameVectorCount(float[] values) => values == null ? 0 : values.Length / 3;

    private string CurrentNetworkSceneName()
    {
        var currentScene = _state.CurrentScene;
        var lastGameplayScene = _state.LastGameplayScene;
        if (SceneRoles.IsBivouac(currentScene) && !string.IsNullOrEmpty(lastGameplayScene))
            return lastGameplayScene;

        return currentScene ?? "";
    }

    /// <summary>
    /// Resets the per-episode sync state on a scene-bound reset point: local sync debug
    /// flags, the hand-pose poll cache, and the rope sub-state. Features reset their own
    /// through OnSceneReset.
    /// </summary>
    internal void ResetSyncState()
    {
        _debugLoggedFirstPlayerFrameCapture = false;
        _debugLoggedFirstClimbotFrameCapture = false;
        _debugLastMissingPlayerFrameLogAt = 0f;
        _debugLastMissingClimbotFrameLogAt = 0f;
        CosmeticInterop.ResetCaches();
        // No reset of freecam detection here: the eagle-eye/Display Route state is
        // driven by native events and persists across scene streaming. Resetting it
        // would make the mod believe we left Display Route (while we're still in it)
        // -> can't place a ping until we re-toggle.
        FingerInterop.ResetCaches();
        _resetRope();
    }

    private static void LogMissingLocalNetFrame(string target, ref float lastLogAt)
    {
        var now = Time.unscaledTime;
        if (now - lastLogAt < NetFrameMissingLogIntervalSeconds) return;

        lastLogAt = now;
        ModLog.Debug($"[NetSync] Waiting for local {target} NetFrame capture while InGame");
    }
}
