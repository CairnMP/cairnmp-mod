using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Roping;
using CairnMultiplayerMod.Internal.Networking;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game.Players;

internal sealed class PlayerStateBroadcaster
{
    private readonly NetworkManager _network;
    private readonly RuntimeState _state;
    private readonly System.Action _resetRope;
    private readonly System.Func<bool> _isGameplaySyncSuspended;
    // Drives the speaking icon on the floating names. Optional: the broadcaster runs fine
    // without voice, the icon simply never lights.
    private readonly System.Func<int, bool> _isSpeaking;

    private float _stateTickTimer;
    private float _boneTickTimer;
    internal string LastComputedStateReason { get; private set; } = "not computed";

    private const float NetFrameMissingLogIntervalSeconds = 3f;
    private bool _debugLoggedFirstPlayerFrameCapture;
    private bool _debugLoggedFirstClimbotFrameCapture;
    private float _debugLastMissingPlayerFrameLogAt;
    private float _debugLastMissingClimbotFrameLogAt;

    internal PlayerStateBroadcaster(
        NetworkManager network,
        RuntimeState state,
        System.Action resetRope,
        System.Func<bool> isGameplaySyncSuspended,
        System.Func<int, bool> isSpeaking = null)
    {
        _network = network;
        _state = state ?? throw new System.ArgumentNullException(nameof(state));
        _resetRope = resetRope ?? throw new System.ArgumentNullException(nameof(resetRope));
        _isGameplaySyncSuspended = isGameplaySyncSuspended
            ?? throw new System.ArgumentNullException(nameof(isGameplaySyncSuspended));
        _isSpeaking = isSpeaking;
    }

    internal void ResetTimers()
    {
        _stateTickTimer = 0f;
        _boneTickTimer = 0f;
    }

    internal void Tick()
    {
        if (_network.IsHandshakeComplete)
        {
            if (_isGameplaySyncSuspended())
            {
                TickSuspendedNetworkPresence();
                return;
            }

            // Keep the durable network presence InGame during additive streaming, but
            // never touch pawn/ghost native objects while Cairn's graph is unavailable.
            if (_state.LocalPlayerState == PlayerState.InGame && !IsGameplayGraphReady())
            {
                _stateTickTimer = 0f;
                _boneTickTimer = 0f;
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


            if (_state.LocalPlayerState == PlayerState.InGame)
            {
                for (var discovered = 0; discovered < 32 && RopeInterop.CheckForNewPiton(out var pitonId, out var pitonPos,
                    out var pitonRot, out var pitonQuality, out var pitonHp, out var pitonItemId); discovered++)
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

                for (var removed = 0; removed < 32 && RopeInterop.CheckForRemovedPiton(out var removedPitonId); removed++)
                {
                    _network.SendPitonRemoved(removedPitonId);
                }
            }

            RemotePlayerManager.Reconcile(_network, _state.LocalPlayerState);
            RemotePlayerManager.UpdateAll(_network, _state.LocalPlayerState, _isSpeaking);
        }
        else
        {
            if (_state.LocalPlayerState == PlayerState.Unknown)
                RemotePlayerManager.ClearAll();
        }
    }

    /// <summary>Cairn owns the gameplay graph during bivouac, so only lifecycle presence is safe.</summary>
    internal void TickSuspendedNetworkPresence()
    {
        _boneTickTimer = 0f;

        _stateTickTimer += Time.unscaledDeltaTime;
        if (_stateTickTimer < Protocol.PlayerStateUpdateIntervalSeconds)
            return;

        _stateTickTimer = 0f;
        _network.SendPlayerState(0f, 0f, 0f, 0f, CurrentNetworkSceneName(), PlayerState.Bivouac);
    }

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
    /// Delays <c>InGame</c> until the scene graph settles because touching addressable ghost
    /// prefabs during a transition can crash Cairn.
    /// </summary>
    internal PlayerState ComputeLocalState()
    {
        if (!_network.IsConnected)
            return Computed(PlayerState.Unknown, "network disconnected");
        if (!_network.IsHandshakeComplete)
            return Computed(PlayerState.Connecting, "handshake incomplete");
        var currentScene = _state.CurrentScene;
        if (currentScene == null)
            return Computed(PlayerState.Connecting, "no current scene");

        if (SceneRoles.IsMainMenuArea(currentScene))
            return Computed(PlayerState.InMenu, $"scene={currentScene}");

        if (SceneRoles.IsLoading(currentScene))
            return Computed(PlayerState.Loading, $"transition scene={currentScene}");

        var hasGameplayContext = SceneRoles.HasGameplayContext(currentScene, _state.LastGameplayScene);

        if (GameLifecycleService.TryGetGameLifecycle(out var lifecycle, out var lifecycleDetail))
        {
            var networkState = MapLifecycleForNetwork(
                lifecycle, MultiplayerPausePatch.IsPauseMenuActive);
            // GlobalGameManager briefly reports Loading while streaming additive world
            // layers. Preserve the semantic session state; Tick's native-ready gate above
            // independently pauses all unsafe pawn and ghost access.
            if (ShouldPreserveInGameDuringStreaming(
                    lifecycle, _state.LocalPlayerState, hasGameplayContext))
                return Computed(PlayerState.InGame, $"transient streaming preserved: {lifecycleDetail}");
            if (networkState != PlayerState.InGame)
                return Computed(networkState, lifecycleDetail);
        }
        else if (SceneRoles.IsBivouac(currentScene))
        {
            return Computed(PlayerState.Loading, "bivouac scene without lifecycle");
        }

        // In a gameplay scene, wait for the graph to be stable before allowing
        // native captures and ghosts. After a death, a camera can be active before
        // the new MC exists; using that camera as an InGame signal would trigger
        // captures on destroyed objects.
        if (_state.TimeSinceLastSceneLoad < 1.0f)
            return Computed(PlayerState.Loading, $"gameplay boundary settling ({_state.TimeSinceLastSceneLoad:0.00}s)");

        if (LocalPlayerInterop.TryGetPose(out _, out _))
            return Computed(PlayerState.InGame, "gameplay graph ready");

        if (_state.LocalPlayerState == PlayerState.InGame && hasGameplayContext)
            return Computed(PlayerState.InGame, "transient missing pawn preserved");

        return Computed(PlayerState.Loading, "local pawn unavailable");
    }

    private PlayerState Computed(PlayerState state, string reason)
    {
        LastComputedStateReason = reason;
        return state;
    }

    private static bool IsGameplayGraphReady()
    {
        if (!GameLifecycleService.TryGetRawGameState(out var lifecycle)) return false;
        if (MapLifecycleForNetwork(lifecycle, MultiplayerPausePatch.IsPauseMenuActive) != PlayerState.InGame)
            return false;
        return LocalPlayerInterop.TryGetPose(out _, out _);
    }

    internal static bool ShouldPreserveInGameDuringStreaming(
        CairnGameLifecycleState lifecycle, PlayerState currentState, bool hasGameplayContext)
        => lifecycle == CairnGameLifecycleState.Loading
           && currentState == PlayerState.InGame
           && hasGameplayContext;

    /// <summary>
    /// Cairn temporarily pushes GlobalGameManager.GameState.Menu for the in-game pause
    /// overlay. In multiplayer the native pause requests are suppressed, so this remains
    /// live gameplay for networking: physics advances and pose snapshots must continue.
    /// A real menu transition still advertises InMenu and all other non-gameplay states
    /// stay behind the loading boundary.
    /// </summary>
    internal static PlayerState MapLifecycleForNetwork(
        CairnGameLifecycleState lifecycle, bool pauseMenuActive)
    {
        if (lifecycle == CairnGameLifecycleState.InGame)
            return PlayerState.InGame;
        if (lifecycle == CairnGameLifecycleState.Menu)
            return pauseMenuActive ? PlayerState.InGame : PlayerState.InMenu;
        return PlayerState.Loading;
    }

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
        => SceneRoles.ResolveNetworkScene(_state.CurrentScene, _state.LastGameplayScene);

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
