using CairnMultiplayer.Shared;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

public partial class Mod
{
    private float _timeSinceLastSceneLoad;
    private float _lampPollTimer;
    private bool _hasLastSentLampState;
    private int _lastSentLampMode;
    private float _cosmeticPollTimer;
    private bool _hasLastSentCosmetic;
    private byte _lastSentCosmeticFlags;
    private float _handPosePollTimer;
    private byte[] _lastSentHandPosePacked;
    private const float NetFrameMissingLogIntervalSeconds = 3f;
    private bool _debugLoggedFirstPlayerFrameCapture;
    private bool _debugLoggedFirstClimbotFrameCapture;
    private float _debugLastMissingPlayerFrameLogAt;
    private float _debugLastMissingClimbotFrameLogAt;

    /// <summary>
    /// Handles periodic state broadcasting, piton checks, and the ghost lifecycle.
    /// Called every frame from OnUpdate().
    /// </summary>
    private void TickPlayerSync()
    {
        // Periodic broadcast of the local player state + sync of remote ghosts.
        if (_network.IsHandshakeComplete)
        {
            if (IsGameplaySyncSuspended())
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

            TickWeatherSync();
            TickLampSync();
            TickCosmeticSync();
            TickHandPoseSync();

            // Check for newly placed pitons (a lower frequency is enough).
            if (LocalState == PlayerState.InGame)
            {
                if (CairnGameApi.CheckForNewPiton(out var pitonId, out var pitonPos,
                    out var pitonRot, out var pitonQuality, out var pitonHp, out var pitonItemId))
                {
                    _network.SendPitonPlaced(pitonId, pitonPos, pitonRot, pitonQuality, pitonHp, pitonItemId);
                }

                if (CairnGameApi.CheckForRemovedPiton(out var removedPitonId))
                {
                    _network.SendPitonRemoved(removedPitonId);
                }
            }

            // Ghost lifecycle: spawn early, keep them alive across transitions,
            // then drive the transforms from the latest packets.
            RemotePlayerManager.Reconcile(_network, LocalState);
            RemotePlayerManager.UpdateAll(_network, LocalState);
        }
        else
        {
            // Disconnected — remove any leftover ghosts.
            if (LocalState == PlayerState.Unknown)
                RemotePlayerManager.ClearAll();
        }
    }

    /// <summary>
    /// Keeps a minimal network presence during the bivouac without touching the
    /// original gameplay graph (no MC, NetFrame, weather, pitons, ghosts).
    /// </summary>
    private void TickSuspendedNetworkPresence()
    {
        _boneTickTimer = 0f;
        _weatherTickTimer = Protocol.WeatherStateUpdateIntervalSeconds;

        _stateTickTimer += Time.unscaledDeltaTime;
        if (_stateTickTimer < Protocol.PlayerStateUpdateIntervalSeconds)
            return;

        _stateTickTimer = 0f;
        _network.SendPlayerState(0f, 0f, 0f, 0f, CurrentNetworkSceneName(), PlayerState.Loading);
    }

    /// <summary>
    /// Sends the local player's body position + the current lifecycle state.
    /// Prefers the real MC transform via PawnManager.MCGameObject; falls back to
    /// Camera.main until the MC has been instantiated.
    /// </summary>
    private void SendLocalPlayerState()
    {
        if (LocalState != PlayerState.InGame)
        {
            _network.SendPlayerState(0f, 0f, 0f, 0f, CurrentNetworkSceneName(), LocalState);
            return;
        }

        Vector3 p; float yaw;
        if (!CairnGameApi.TryGetLocalPlayerPose(out p, out yaw))
        {
            var cam = Camera.main;
            if (cam == null) return;
            var t = cam.transform;
            p = t.position;
            yaw = t.eulerAngles.y;
        }
        _network.SendPlayerState(p.x, p.y, p.z, yaw, CurrentNetworkSceneName(), LocalState);
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
    private PlayerState ComputeLocalState()
    {
        if (!_network.IsConnected)
            return PlayerState.Unknown;
        if (!_network.IsHandshakeComplete)
            return PlayerState.Connecting;
        if (_currentScene == null)
            return PlayerState.Connecting;

        // Main-menu category — covers "MainMenu" and "MainMenuBackgroundsBase".
        if (_currentScene.StartsWith("MainMenu"))
            return PlayerState.InMenu;

        if (_currentScene == "LoadingScreen" || _currentScene == "CommonBaseScene")
            return PlayerState.Loading;

        if (CairnGameApi.TryGetGameLifecycle(out var lifecycle, out _))
        {
            if (lifecycle == CairnGameLifecycleState.Menu)
                return PlayerState.InMenu;

            if (lifecycle != CairnGameLifecycleState.InGame)
                return PlayerState.Loading;
        }
        else if (IsNonGameplayScene(_currentScene))
        {
            return PlayerState.Loading;
        }

        // In a gameplay scene, wait for the graph to be stable before allowing
        // native captures and ghosts. After a death, a camera can be active before
        // the new MC exists; using that camera as an InGame signal would trigger
        // captures on destroyed objects.
        if (_timeSinceLastSceneLoad < 1.0f)
            return PlayerState.Loading;

        if (CairnGameApi.TryGetLocalPlayerPose(out _, out _))
            return PlayerState.InGame;

        return PlayerState.Loading;
    }

    /// <summary>
    /// Captures and sends the local native Netplay frames at animation rate.
    /// </summary>
    private void SendLocalNetFrames()
    {
        if (LocalState != PlayerState.InGame) return;

        if (CairnGameApi.TryCaptureLocalPlayerFrame(out var playerFrame))
        {
            if (!_debugLoggedFirstPlayerFrameCapture)
            {
                _debugLoggedFirstPlayerFrameCapture = true;
                Mod.LogDebug($"[NetSync] First local player NetFrame captured positions={FrameVectorCount(playerFrame.Positions)} eulers={FrameVectorCount(playerFrame.Eulers)} flags=0x{playerFrame.Flags:X2}");
            }
            _network.SendPlayerFrame(playerFrame);
        }
        else
        {
            LogMissingLocalNetFrame("player", ref _debugLastMissingPlayerFrameLogAt);
        }

        if (CairnGameApi.TryCaptureLocalClimbotFrame(out var climbotFrame))
        {
            if (!_debugLoggedFirstClimbotFrameCapture)
            {
                _debugLoggedFirstClimbotFrameCapture = true;
                Mod.LogDebug($"[NetSync] First local climbot NetFrame captured positions={FrameVectorCount(climbotFrame.Positions)} eulers={FrameVectorCount(climbotFrame.Eulers)} flags=0x{climbotFrame.Flags:X2}");
            }
            _network.SendClimbotFrame(climbotFrame);
        }
        else
        {
            LogMissingLocalNetFrame("climbot", ref _debugLastMissingClimbotFrameLogAt);
        }
    }

    private static int FrameVectorCount(float[] values) => values == null ? 0 : values.Length / 3;

    private static bool IsNonGameplayScene(string sceneName)
    {
        return sceneName == "BivouacIndoor";
    }

    private string CurrentNetworkSceneName()
    {
        if (IsNonGameplayScene(_currentScene) && !string.IsNullOrEmpty(_lastGameplayScene))
            return _lastGameplayScene;

        return _currentScene ?? "";
    }

    private void ResetPlayerSyncDebugState()
    {
        _debugLoggedFirstPlayerFrameCapture = false;
        _debugLoggedFirstClimbotFrameCapture = false;
        _debugLastMissingPlayerFrameLogAt = 0f;
        _debugLastMissingClimbotFrameLogAt = 0f;
        _lampPollTimer = 0f;
        _hasLastSentLampState = false;
        _lastSentLampMode = 0;
        _cosmeticPollTimer = 0f;
        _hasLastSentCosmetic = false;
        _lastSentCosmeticFlags = 0;
        CairnGameApi.ResetLocalCosmeticsCache();
        _handPosePollTimer = 0f;
        _lastSentHandPosePacked = null;
        CairnGameApi.ResetLocalLampStateCache();
        // No reset of freecam detection here: the eagle-eye/Display Route state is
        // driven by native events and persists across scene streaming. Resetting it
        // would make the mod believe we left Display Route (while we're still in it)
        // -> can't place a ping until we re-toggle.
        CairnGameApi.ResetFingerSyncCache();
        ResetTimeSyncState();
        ResetRopeCoupleState();
    }

    /// <summary>
    /// Polls the local lamp state and broadcasts to the other players whenever it
    /// changes. No message until we've been able to read it at least once, to avoid
    /// sending a misleading "off" during loads.
    /// </summary>
    private void TickLampSync()
    {
        if (LocalState != PlayerState.InGame) return;

        _lampPollTimer += Time.unscaledDeltaTime;
        if (_lampPollTimer < Protocol.LampStatePollIntervalSeconds)
            return;
        _lampPollTimer = 0f;

        if (!CairnGameApi.TryGetLocalLampState(out var lightMode))
            return;

        // The lamp int also carries (high bits, no new packet):
        //  - bits 8-15  : stick anchor mode (Locator/Default) -> ApplyGhostStickByAnchorMode
        //  - bits 16-24 : outfit bitfield (active meshes) -> ApplyGhostOutfitBits
        int anchorMode = 0;
        CairnGameApi.TryGetLocalStickAnchorMode(out anchorMode);
        int outfitBits = CairnGameApi.GetLocalOutfitBits();
        int packed = (lightMode & 0xFF) | ((anchorMode & 0xFF) << 8)
                     | ((outfitBits & CairnGameApi.OutfitBitsMask) << 16);

        if (_hasLastSentLampState && _lastSentLampMode == packed)
            return;

        _lastSentLampMode = packed;
        _hasLastSentLampState = true;
        _network.SendLampState(packed);
    }

    /// <summary>
    /// Polls the local cosmetic state (glowing gloves for now) and broadcasts it
    /// whenever it changes. Same logic as the lamp: no send until we've read it at
    /// least once, and only on change (cosmetics are nearly static).
    /// </summary>
    private void TickCosmeticSync()
    {
        if (LocalState != PlayerState.InGame) return;

        _cosmeticPollTimer += Time.unscaledDeltaTime;
        if (_cosmeticPollTimer < Protocol.CosmeticStatePollIntervalSeconds)
            return;
        _cosmeticPollTimer = 0f;

        if (!CairnGameApi.TryGetLocalCosmetics(out var flags))
            return;

        if (_hasLastSentCosmetic && _lastSentCosmeticFlags == flags)
            return;

        _lastSentCosmeticFlags = flags;
        _hasLastSentCosmetic = true;
        _network.SendCosmeticState(flags);
    }

    /// <summary>
    /// Captures the local finger pose and broadcasts it at ~12 Hz, only when it
    /// changes (motionless fingers generate no traffic).
    /// </summary>
    private void TickHandPoseSync()
    {
        if (LocalState != PlayerState.InGame) return;

        _handPosePollTimer += Time.unscaledDeltaTime;
        if (_handPosePollTimer < Protocol.HandPosePollIntervalSeconds)
            return;
        _handPosePollTimer = 0f;

        if (!CairnGameApi.TryCaptureLocalFingerPose(out var packed))
            return;

        if (BytesEqual(_lastSentHandPosePacked, packed))
            return;

        _lastSentHandPosePacked = packed;
        _network.SendHandPose(packed);
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static void LogMissingLocalNetFrame(string target, ref float lastLogAt)
    {
        var now = Time.unscaledTime;
        if (now - lastLogAt < NetFrameMissingLogIntervalSeconds) return;

        lastLogAt = now;
        Mod.LogDebug($"[NetSync] Waiting for local {target} NetFrame capture while InGame");
    }
}
