using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CairnMultiplayer.Shared;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(CairnMultiplayerMod.Bootstrap.Mod), "Cairn Multiplayer Mod", "1.0.0", "CairnModTeam")]
[assembly: MelonGame("TheGameBakers", "Cairn")]

namespace CairnMultiplayerMod.Bootstrap;

public partial class Mod : MelonMod
{
    public static Mod Instance { get; private set; }
    public static MelonLogger.Instance Log => Instance.LoggerInstance;

    /// <summary>Verbose diagnostic logs (scenes, native object resolution, etc.). OFF by
    /// default to keep the console clean; set to true to debug.</summary>
    public static bool VerboseLogging;

    /// <summary>Diagnostic log: writes ONLY if VerboseLogging is enabled.</summary>
    public static void LogDebug(string message)
    {
        if (VerboseLogging) Log.Msg(message);
    }

    private NetworkManager _network;
    private SteamLobbyManager _lobby;
    private IMultiplayerPanel _panel;
    private ChatController _chat;
    private string _currentScene;
    private readonly HashSet<string> _loadedScenes = new(StringComparer.Ordinal);
    private string _lastGameplayScene;
    private float _timeSinceLastSceneLoad;
    private Key _connectKey;
    private Key _disconnectKey;
    private bool _gameplaySyncSuspended;
    private bool _netplaySetFramePatchPausedForBivouac;
    // DEFERRED resume of the SetFrame patch after leaving a bivouac: the sealing /
    // disk write of the native save package can finish a few moments AFTER the bivouac
    // flag drops. Re-enabling SetFrame injection right in that window left the package
    // disposed -> 1 save OK then nothing. So we keep the patch paused for a few more
    // seconds (0 = no resume scheduled).
    private float _setFramePatchResumeAt;
    private const float SetFramePatchResumeGraceSeconds = 3f;
    private float _nextBivouacDebugLogAt;
    private float _bivouacSuspendedSince;
    private const float BivouacDebugLogIntervalSeconds = 3f;
    // Diagnostic: watch window after leaving a bivouac to check whether the
    // ghosts reappear (the double-gate deadlock case).
    private float _bivouacRecoveryWatchUntil;
    private float _nextBivouacRecoveryLogAt;
    private const float BivouacRecoveryWatchSeconds = 30f;
    private const float BivouacRecoveryLogIntervalSeconds = 3f;
    // Anti-lockup guard for the bivouac: if the native flag stays stuck on exit,
    // we force the sync to resume to avoid a permanent desync.
    private UnityEngine.Vector3 _bivouacSuspendPawnPos;
    private bool _hasBivouacSuspendPawnPos;
    private float _bivouacStuckSince;
    private const float BivouacStuckResumeSeconds = 8f;
    private const float BivouacStuckMoveThresholdSqr = 2.25f; // ~1.5 m of movement

    // Tracks the progress of lobby creation/join to display a status that
    // evolves during the wait (Hetzner auto-provisioning = 1-3 min).
    private DateTime? _connectingStart;
    private string    _connectingVerb = "Creating lobby"; // "Creating lobby" | "Joining lobby"
    private string    _lastLobbyError;

    // Queue of actions to run on the Unity thread — async callbacks (ContinueWith)
    // run on the ThreadPool and CANNOT touch Unity objects (TMP, Image, ...)
    // directly under IL2CPP.
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();

    /// <summary>Schedules an action to run on the next Update tick on the Unity thread.</summary>
    private void RunOnMainThread(Action action) => _mainThreadActions.Enqueue(action);

    public NetworkManager Network => _network;
    public SteamLobbyManager Lobby => _lobby;
    public PlayerState LocalState { get; private set; } = PlayerState.Unknown;

    // Per-feature sync components, constructed in OnInitializeMelon and ticked from OnUpdate.
    internal PlayerStateBroadcaster Player { get; private set; }
    internal WeatherStateBroadcaster Weather { get; private set; }
    internal TimeStateBroadcaster Clock { get; private set; }
    internal RopeCoupleController Rope { get; private set; }
    internal StartGameFlow StartGame { get; private set; }

    // Scene state read by the sync components (kept authoritative here, on the mod core).
    internal string CurrentScene => _currentScene;
    internal string LastGameplayScene => _lastGameplayScene;
    internal float TimeSinceLastSceneLoad => _timeSinceLastSceneLoad;

    public override void OnInitializeMelon()
    {
        Instance = this;
        CrashReporter.Init();
        Il2CppExceptionCapture.Install();
        ModConfig.Register();
        VerboseLogging = ModConfig.VerboseLogging.Value;
        NetplaySetFramePatch.InstallNetplaySetFramePatch();
        BivouacDiagnostics.InstallBivouacDiagnosticsPatches();
        RopeTeamFallPatch.InstallRopeTeamFallPatch();
        MultiplayerPausePatch.InstallMultiplayerPausePatch();
        FreeRoamUnlockPatch.InstallFreeRoamUnlockPatch();
        SavegamePitonGuardPatch.InstallSavegamePitonGuardPatch();

        _connectKey = ParseKey(ModConfig.ConnectKey.Value, Key.F5);
        _disconnectKey = ParseKey(ModConfig.DisconnectKey.Value, Key.F6);
        _network = new NetworkManager();
        _lobby   = new SteamLobbyManager();
        _panel = MultiplayerPanelFactory.Create();

        // Per-feature sync components. They read shared state via Mod.Instance and are
        // ticked, in this exact order, from OnUpdate.
        Weather = new WeatherStateBroadcaster(_network, _lobby);
        Clock = new TimeStateBroadcaster(_network, _lobby);
        Rope = new RopeCoupleController(_network);
        Player = new PlayerStateBroadcaster(_network);
        StartGame = new StartGameFlow(_panel);

        // In-game chat + admin commands. The router checks the host role at dispatch;
        // command feedback is displayed as local system lines.
        var commandRouter = new CommandRouter(_network, () => _lobby?.IsHost == true,
            line => _chat?.AddSystemLine(line));
        // canChat also requires the game to NOT be paused (Cairn pauses via timeScale=0).
        // Otherwise, opening the chat then pausing would leave the overlay open forcing the
        // input freeze -> player stuck after unpausing. Here, when paused the chat
        // auto-closes (ChatController.Update) and restores input.
        _chat = new ChatController(_network, commandRouter,
            () => LocalState == PlayerState.InGame && Time.timeScale > 0f);

        // Steam Matchmaking → UI wiring. The Steam callbacks are pumped by Cairn
        // itself on the Unity thread, so there's no marshalling to do.
        _lobby.OnLobbyEntered += id =>
        {
            _connectingStart = null;
            _lastLobbyError = null;
            var code = _lobby.CurrentRoomCode;
            _panel.SetCurrentLobbyName(_lobby.CurrentLobbyName);
            _panel.SetStatus(string.IsNullOrEmpty(code) ? "Connected" : $"Connected — code {code}", true);
            _network.StartSteamTransport(_lobby);
        };
        _lobby.OnLobbyError += err =>
        {
            _connectingStart = null;
            _lastLobbyError = err;
            _panel.SetStatus($"Failed: {err}", _lobby != null && _lobby.IsInLobby);
        };
        _lobby.OnLobbyLeft += () =>
        {
            _network.Disconnect();
            WeatherApi.ResetRemoteWeatherSyncState();
            RemotePlayerManager.ClearAll();
            PingMarkerManager.ClearAll();
            Rope.ClearLinks();
            _gameplaySyncSuspended = false;
            ResumeNetplaySetFramePatchAfterBivouac();
            _panel.SetStatus("Disconnected", false);
        };
        _lobby.OnMembersChanged += () =>
        {
            // The ConnectedScreen rebuilds its list on every Tick from _lobby.Members,
            // but we also push a short status so the footer reflects the event.
            if (!_lobby.IsInLobby) return;
            _network.RefreshSteamLobbyMembers(_lobby);
            _panel.SetStatus($"{_lobby.Members.Count} player(s) in lobby", true);
        };
        _lobby.OnStartRequested += StartGame.Begin;

        // Menu button: the Multiplayer click hides the Cairn menu and opens the panel.
        MainMenuMultiplayerButton.Bind(_panel);

        _panel.OnHostRequested            += OnHostRequested;
        _panel.OnJoinByCodeRequested      += OnJoinByCodeRequested;
        _panel.OnBrowseRequested          += OnBrowseRequested;
        _panel.OnJoinByLobbyIdRequested   += OnJoinByLobbyIdRequested;
        _panel.OnDisconnectRequested      += OnDisconnectRequested;
        _panel.OnStartRequested           += OnStartRequested;
        _panel.OnPanelClosed              += MainMenuMultiplayerButton.RestoreModeSelect;

        _network.OnHandshakeAck += () =>
            _panel.SetStatus($"Connected to {_network.ServerName} (id={_network.LocalPlayerId})", true);
        _network.OnHandshakeRejected += reason =>
            _panel.SetStatus($"Rejected: {reason}", false);
        _network.OnDisconnected += _ =>
        {
            WeatherApi.ResetRemoteWeatherSyncState();
            Rope.ClearLinks();
            _gameplaySyncSuspended = false;
            ResumeNetplaySetFramePatchAfterBivouac();
        };
        _network.OnPlayerJoined += (id, name) =>
        {
            _panel.SetStatus($"Player joined: {name}", _network.IsConnected);
            _network.RemotePlayers.TryGetValue(id, out var rp);
            RemotePlayerManager.OnPlayerJoined(id, name, rp);
        };
        _network.OnPlayerLeft += id =>
        {
            _panel.SetStatus($"Player {id} left", _network.IsConnected);
            RemotePlayerManager.OnPlayerLeft(id);
            RopeLinkState.RemovePlayer(id);
        };
        // Roping: apply each authoritative clip/unclip to the global link state.
        _network.OnRopeClip += (from, target, clip) => RopeLinkState.Apply(from, target, clip);

        // Piton sync: spawn the pitons placed by other players via Lifeline.AddPiton.
        _network.OnPitonPlaced += pkt =>
        {
            if (IsGameplaySyncSuspended())
                return;

            RopeApi.SpawnRemotePiton(pkt.PitonId,
                new UnityEngine.Vector3(pkt.PosX, pkt.PosY, pkt.PosZ),
                new UnityEngine.Quaternion(pkt.RotX, pkt.RotY, pkt.RotZ, pkt.RotW),
                pkt.Quality, pkt.PitonHp, pkt.ItemId);
        };
        _network.OnPitonRemoved += pkt =>
        {
            if (IsGameplaySyncSuspended())
                return;

            RopeApi.RemoveRemotePiton(pkt.PitonId);
        };
        _network.OnWeatherState += pkt =>
        {
            if (_lobby?.IsHost == true)
                return;
            if (IsGameplaySyncSuspended())
                return;

            WeatherApi.ApplyRemoteWeather(pkt.State);
        };

        // Ping marker placed by another player: display a colored HUD waypoint.
        _network.OnPingPlaced += pkt =>
            PingMarkerManager.Spawn(pkt.FromPlayerId, new UnityEngine.Vector3(pkt.PosX, pkt.PosY, pkt.PosZ));

        // Authoritative day time received from the host: applied every frame client-side.
        _network.OnTimeState += pkt => Clock.ApplyRemoteTimeState(pkt);

        // Authoritative game launch by the server: queues the packet, applies it in
        // Update when the MainMenu scene is active.
        _network.OnStartGameReceived += pkt =>
        {
            StartGame.Begin(pkt);
        };

        LoggerInstance.Msg("===========================================");
        LoggerInstance.Msg($"  Cairn Multiplayer Mod v{Protocol.GameVersion} loaded!");
        LoggerInstance.Msg($"  Keybinds: {_connectKey} (panel) / {_disconnectKey} (disconnect) / N (toggle player names)");
        LoggerInstance.Msg("===========================================");
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        _loadedScenes.Add(sceneName);
        if (IsGameplayRootScene(sceneName))
            _lastGameplayScene = sceneName;

        _currentScene = sceneName;
        _timeSinceLastSceneLoad = 0f;
        LogDebug($"Scene loaded: [{buildIndex}] {sceneName}");

        // FreeRoam unlock: active ONLY at the MainMenu (forcing the flag during boot
        // or in game sends the game onto an unready FreeRoam init path -> black screen).
        FreeRoamUnlockPatch.SetFreeRoamUnlockActive(sceneName == "MainMenu");

        if (sceneName != "MainMenu")
            _panel.DestroyResources();

        if (IsNonGameplayScene(sceneName))
        {
            LogDebug($"[State] Scene {sceneName} treated as Loading for multiplayer");
            LogBivouacDebug("scene-loaded");
        }
        if (IsSceneBoundCacheResetPoint(sceneName))
        {
            ResetSceneBoundSyncState();
            // Note: we do NOT clear the pings here — they're positioned in the world
            // and expire on their own (15 s). Clearing them on every scene stream would
            // make them disappear while we're still in the area.
            if (ShouldClearRemotePlayersOnSceneLoad(sceneName))
                RemotePlayerManager.ClearAll();
        }
        MainMenuMultiplayerButton.OnSceneLoaded(sceneName);
        // Auto-disconnect when returning to the MainMenu from gameplay.
        // Cleans up the ghosts and lets the player reconnect cleanly.
        if (sceneName == "MainMenu" && _network.IsConnected && (_lobby == null || !_lobby.IsInLobby))
        {
            LoggerInstance.Msg("[State] Returned to MainMenu — auto-disconnecting");
            _network.Disconnect();
            RemotePlayerManager.ClearAll();
            Rope.ClearLinks();
            _panel.SetStatus("Disconnected (returned to menu)", false);
        }
        else if (sceneName == "MainMenu" && _lobby != null && _lobby.IsInLobby)
        {
            RemotePlayerManager.ClearAll();
            Rope.ClearLinks();
        }
    }

    public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
    {
        _loadedScenes.Remove(sceneName);

        var wasCurrentScene = string.Equals(_currentScene, sceneName, StringComparison.Ordinal);
        if (wasCurrentScene)
            _currentScene = ResolveCurrentSceneAfterUnload(sceneName);

        if (!IsNonGameplayScene(sceneName))
            return;

        _timeSinceLastSceneLoad = 0f;
        LogDebug($"Scene unloaded: [{buildIndex}] {sceneName}");
        LogDebug($"[State] Scene {sceneName} overlay unloaded for multiplayer");
        ResetSceneBoundSyncState();
    }

    private static bool IsNonGameplayScene(string sceneName)
    {
        return sceneName == "BivouacIndoor";
    }

    private static bool IsSceneBoundCacheResetPoint(string sceneName)
    {
        return sceneName == "LoadingScreen"
            || sceneName == "CommonBaseScene"
            || sceneName == "MainMenu"
            || IsGameplayRootScene(sceneName);
    }

    private static bool ShouldClearRemotePlayersOnSceneLoad(string sceneName)
    {
        return sceneName == "LoadingScreen"
            || sceneName == "CommonBaseScene"
            || sceneName == "MainMenu"
            || IsGameplayRootScene(sceneName);
    }

    private void ResetSceneBoundSyncState()
    {
        SceneCache.ResetSceneCaches();
        Player.ResetSyncState();
        Player.ResetTimers();
        Weather.ResetTimer();
    }

    internal bool IsGameplaySyncSuspended()
    {
        return _network?.IsConnected == true && (_gameplaySyncSuspended || ShouldSuspendGameplaySync());
    }

    private void UpdateGameplaySyncSuspension()
    {
        if (_network == null || !_network.IsConnected)
        {
            if (_gameplaySyncSuspended)
                LogBivouacDebug("network-disconnected");

            _gameplaySyncSuspended = false;
            ResumeNetplaySetFramePatchAfterBivouac();
            return;
        }

        var shouldSuspend = ShouldSuspendGameplaySync();
        if (shouldSuspend == _gameplaySyncSuspended)
            return;

        _gameplaySyncSuspended = shouldSuspend;
        if (_gameplaySyncSuspended)
        {
            LoggerInstance.Msg("[State] Gameplay sync suspended for bivouac");
            _bivouacSuspendedSince = Time.unscaledTime;
            _nextBivouacDebugLogAt = 0f;
            PauseNetplaySetFramePatchForBivouac();
            ResetGameplaySyncTimers();
            WeatherApi.ResetRemoteWeatherSyncState();
            RemotePlayerManager.ClearAll();
            // Release the native rope-team anchors: ClearAll destroys the ghosts, so a rope
            // left pinned to their harness would point into the void. We KEEP the logical link
            // (RopeLinkState) — it'll be re-anchored on exit when the partner's ghost returns.
            // TickRopeCouple doesn't run during a bivouac, hence this release here.
            RopeApi.ReleaseAllRopeTeamAnchors();
            SetLocalState(PlayerState.Loading);
            // Remember the pawn's position on entry: used by the anti-lockup guard
            // (a pawn that has moved = we're climbing again).
            _hasBivouacSuspendPawnPos = LocalPlayerApi.TryGetLocalPlayerPose(out _bivouacSuspendPawnPos, out _);
            _bivouacStuckSince = 0f;
            LogBivouacDebug("enter");
            return;
        }

        LogBivouacDebug("exit");
        // Arm the recovery watch: we want to see, on each client, whether the remote
        // ghosts come back after leaving the bivouac or whether we stay stuck (one side
        // never InGame, or a mutual deadlock at Loading).
        _bivouacRecoveryWatchUntil = Time.unscaledTime + BivouacRecoveryWatchSeconds;
        _nextBivouacRecoveryLogAt = 0f;
        _hasBivouacSuspendPawnPos = false;
        _bivouacStuckSince = 0f;
        ResumeNetplaySetFramePatchAfterBivouac(immediate: false);
        LoggerInstance.Msg("[State] Gameplay sync resumed");
        ResetSceneBoundSyncState();
    }

    private bool ShouldSuspendGameplaySync()
    {
        if (IsNonGameplayScene(_currentScene))
        {
            _bivouacStuckSince = 0f;
            return true;
        }

        if (!GameLifecycleService.TryGetGameLifecycle(out var lifecycle, out _))
        {
            _bivouacStuckSince = 0f;
            return false;
        }

        if (lifecycle != CairnGameLifecycleState.Bivouac)
        {
            _bivouacStuckSince = 0f;
            return false;
        }

        // lifecycle == Bivouac, deduced from the BivouacManager flag. Guard against
        // a flag stuck on exit (the cause of the reported permanent desync): if
        // GlobalGameManager already reports InGame, we're on a gameplay scene, and the
        // pawn has moved since entry (we're climbing again), the flag is stale. We
        // require ~8 s of persistence to avoid confusing it with a real bivouac
        // entry/exit transition.
        if (IsBivouacFlagLikelyStuck())
        {
            if (_bivouacStuckSince <= 0f)
                _bivouacStuckSince = Time.unscaledTime;

            if (Time.unscaledTime - _bivouacStuckSince >= BivouacStuckResumeSeconds)
            {
                LoggerInstance.Warning(
                    "[State] Bivouac flag appears stuck (game=InGame, pawn moved) — forcing gameplay sync resume");
                _bivouacStuckSince = 0f;
                return false;
            }

            return true;
        }

        _bivouacStuckSince = 0f;
        return true;
    }

    /// <summary>
    /// Heuristic: the bivouac flag is probably stuck if the game itself reports
    /// InGame, we're on a gameplay scene, and the pawn has moved significantly since
    /// entering the bivouac. Conservative by design: if any of the signals is
    /// missing, we assume a real bivouac.
    /// </summary>
    private bool IsBivouacFlagLikelyStuck()
    {
        if (!IsGameplayRootScene(_currentScene))
            return false;

        if (!GameLifecycleService.TryGetRawGameState(out var raw) || raw != CairnGameLifecycleState.InGame)
            return false;

        if (!_hasBivouacSuspendPawnPos)
            return false;

        if (!LocalPlayerApi.TryGetLocalPlayerPose(out var pos, out _))
            return false;

        return (pos - _bivouacSuspendPawnPos).sqrMagnitude >= BivouacStuckMoveThresholdSqr;
    }

    private string ResolveCurrentSceneAfterUnload(string unloadedScene)
    {
        foreach (var scene in _loadedScenes)
        {
            if (IsGameplayRootScene(scene))
                return scene;
        }

        if (!string.Equals(_lastGameplayScene, unloadedScene, StringComparison.Ordinal))
            return _lastGameplayScene;

        return null;
    }

    private static bool IsGameplayRootScene(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName) || !char.IsDigit(sceneName[0]))
            return false;

        return !sceneName.EndsWith("_Holds", StringComparison.Ordinal)
            && !sceneName.Contains("_Holds", StringComparison.Ordinal)
            && !sceneName.EndsWith("_Gameplay", StringComparison.Ordinal)
            && !sceneName.EndsWith("_Art", StringComparison.Ordinal)
            && !sceneName.EndsWith("_Audio", StringComparison.Ordinal)
            && !sceneName.EndsWith("_Camera", StringComparison.Ordinal)
            && !sceneName.EndsWith("_LOD", StringComparison.Ordinal)
            && !sceneName.EndsWith("_AlwaysLoaded", StringComparison.Ordinal);
    }

    public override void OnUpdate()
    {
        _timeSinceLastSceneLoad += Time.unscaledDeltaTime;

        // Drain the queue of actions coming from async callbacks — must run first so
        // that post-network UI updates are visible on the very next frame.
        while (_mainThreadActions.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex)
            {
                LoggerInstance.Error($"[MainQueue] Action failed: {ex}");
                CrashReporter.ReportCaughtExceptionOnce(ex, "Mod.MainQueue");
            }
        }

        // PANIC failsafe (F10): force-unblock inputs + close the chat, whatever the state.
        // Read from the raw keyboard device (never affected by the block) and placed BEFORE
        // any early return in OnUpdate (bivouac, etc.) so it's always reachable.
        var panicKeyboard = Keyboard.current;
        if (panicKeyboard != null && panicKeyboard.f10Key.wasPressedThisFrame)
        {
            _chat?.ForceClose();
            InputApi.ForceClearInputBlock();
            LoggerInstance.Msg("[CairnMP] Panic: input force-cleared + chat closed (F10)");
        }

        // Keeps inputs frozen while typing in the chat + closes if we leave the game.
        _chat?.Update();

        // While typing in the chat, we ALSO block the mod's shortcuts (E rope, F7/F8,
        // connect, ping...) — otherwise typing text would trigger actions. The game
        // itself is blocked at the InputManager level via ReconcileGameplayInput.
        // Together = total block.
        bool chatTyping = _chat?.IsTyping == true;

        // Update the button injection in the main menu
        if (_currentScene == "MainMenu")
        {
            MainMenuMultiplayerButton.OnUpdate();
            // Unlock FreeRoam: force the tweakable field as soon as it's loaded (no-op
            // once it succeeds). Complements the Harmony postfix on the public property.
            FreeRoamUnlockPatch.TryForceFreeRoamTweakableField();
            // Unhide the FreeRoam mode in the difficulty list (isHidden=false).
            FreeRoamUnlockPatch.TryUnhideFreeRoamDifficulty();
        }

        // Pump the managed Steam callback queue (also handles deferred init).
        _lobby?.Pump(Time.unscaledDeltaTime);

        // Lock the gameplay layer before processing network packets.
        UpdateGameplaySyncSuspension();
        // Deferred resume of the SetFrame patch (post-bivouac grace window) — must run
        // every frame, including during suspension (it self-cancels then).
        UpdateDeferredSetFramePatchResume();

        // Process network events
        _network.Update();

        // Expire the ping markers (always runs, even during a bivouac).
        PingMarkerManager.Update();

        // Ping placement in free camera — runs before the gameplay suspension because
        // freecam/photo mode can be treated as non-gameplay.
        if (!chatTyping) TickPingInput();

        // Name toggle (N) — placed BEFORE the bivouac/photo suspension return so it stays
        // reachable in photo mode (where gameplay is suspended).
        if (!chatTyping) TickNameToggleInput();

        // Injection of the "N" row into the native photo-mode legend. The clone is
        // instantiated under an inactive parent then stripped of its non-visual components
        // (otherwise the duplicated input handlers block the game); injected only when photo
        // mode is actually open.
        try { PhotoModeNamesRow.Tick(); }
        catch (Exception ex) { LoggerInstance.Error($"[PhotoNames] tick failed: {ex.Message}"); }

        // Time + sleep sync — must run BEFORE the bivouac suspension (the bivouac is
        // exactly when we sleep and accelerate time).
        try
        {
            Clock.Tick();
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"[TimeSync] tick failed: {ex.Message}");
        }

        // During a bivouac, Cairn itself drives the pawn, the camera and the taping
        // hands. The mod only keeps a minimal network presence.
        if (_gameplaySyncSuspended)
        {
            TickBivouacDebug();
            Player.TickSuspendedNetworkPresence();
            TickConnectingStatus();
            return;
        }

        // Handles the cursor blink + lobby refresh for the Canvas connection panel.
        _panel.Tick(Time.unscaledDeltaTime);

        // Recompute the local lifecycle state from the scene + handshake + MC.
        var newState = Player.ComputeLocalState();
        SetLocalState(newState);

        // Diagnostic: watch for the sync recovering after a bivouac.
        TickBivouacRecoveryDebug();

        // Game launch flow
        StartGame.Tick();

        // Periodic broadcast of the local player state + sync of remote ghosts.
        try
        {
            Player.Tick();
        }
        catch (Exception ex)
        {
            LoggerInstance.Error(ex.ToString());
            CrashReporter.ReportCaughtExceptionOnce(ex, "Mod.TickPlayerSync");
        }

        // Progressive waiting status during lobby creation/join (provisioning).
        TickConnectingStatus();

        // Long-distance post-teleport settle (prevents falling into the void + fixes the
        // zone-load repositioning). No-op if no teleport is pending.
        TeleportApi.TickTeleportSettle(LocalState == PlayerState.InGame);

        // Inter-player roping: clip detection (E) + maintenance of the NATIVE rope team.
        // No more cosmetic rope: the lifeline's native rope (clipped to a mobile piton
        // placed on the partner, Episure system) is both the visual AND the belay.
        try
        {
            if (!chatTyping) Rope.Tick();   // E must not clip while typing
        }
        catch (Exception ex) { LoggerInstance.Error($"[RopeCouple] tick failed: {ex.Message}"); }

        // Keyboard shortcuts via the new Input System
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        // Chat open -> no mod shortcut passes through (total block).
        if (chatTyping) return;

        if (keyboard[_connectKey].wasPressedThisFrame)
        {
            if (_panel.IsVisible)
            {
                _panel.Hide();
            }
            else
            {
                if (_currentScene != "MainMenu")
                {
                    LoggerInstance.Msg("[CairnMP] Multiplayer panel is only available from the main menu.");
                    return;
                }

                MainMenuMultiplayerButton.HideModeSelect();
                _panel.Show();
            }
        }

        if (keyboard[_disconnectKey].wasPressedThisFrame)
        {
            if (_network.IsConnected)
            {
                OnDisconnectRequested();
            }
        }

        // While the multiplayer panel is open, reassert the blocking of the menu's action maps
        // to prevent any background navigation (Delete, arrows, back).
        if (_panel != null && _panel.IsVisible)
            InputApi.BlockMainMenuActionMaps();
    }

    private static Key ParseKey(string name, Key fallback)
    {
        if (Enum.TryParse<Key>(name, true, out var result))
            return result;
        return fallback;
    }

    private float _pingCooldownUntil;

    /// <summary>
    /// In free camera, a left click (or R1 PS5 / RB Xbox = rightShoulder) places a
    /// ping at the aimed spot. Immediate local display + network send.
    /// </summary>
    private void TickPingInput()
    {
        if (_network == null)
            return;

        if (!FreecamApi.TryIsFreecamActive(out var freecamActive) || !freecamActive)
            return;

        if (Time.unscaledTime < _pingCooldownUntil)
            return;

        var pressed = Mouse.current?.leftButton.wasPressedThisFrame == true
                      || Gamepad.current?.rightShoulder.wasPressedThisFrame == true;
        if (!pressed)
            return;

        // We always aim in the camera's direction (screen center) — simple and
        // consistent for keyboard/mouse as well as gamepad.
        if (!FreecamApi.TryComputePingPoint(out var point))
            return;

        _pingCooldownUntil = Time.unscaledTime + Protocol.PingCooldownSeconds;

        // Immediate local display (visible even in solo). The sender ignores the server
        // echo of its own id. The network send only happens if we're connected.
        PingMarkerManager.Spawn(_network.LocalPlayerId, point);
        if (_network.IsHandshakeComplete)
            _network.SendPingPlaced(point);
        LoggerInstance.Msg($"[Ping] Placed @ ({point.x:F1},{point.y:F1},{point.z:F1})");
    }

    /// <summary>
    /// N key: toggles the display of remote players' name plates. Works both in game
    /// and in photo mode (the hint then appears at the bottom left via PhotoModeHud).
    /// Read from the raw keyboard device.
    /// </summary>
    private void TickNameToggleInput()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb[Key.N].wasPressedThisFrame)
        {
            var shown = RemotePlayerManager.ToggleNames();
            LoggerInstance.Msg($"[CairnMP] Player names {(shown ? "shown" : "hidden")} (N)");
        }
    }

    private void ResetGameplaySyncTimers()
    {
        Player.ResetSyncState();
        Player.ResetTimers();
        Weather.ResetTimer();
    }

    private void TickBivouacDebug()
    {
        if (Time.unscaledTime < _nextBivouacDebugLogAt)
            return;

        _nextBivouacDebugLogAt = Time.unscaledTime + BivouacDebugLogIntervalSeconds;
        LogBivouacDebug("heartbeat");
    }

    private void LogBivouacDebug(string phase)
    {
        var elapsed = _bivouacSuspendedSince > 0f
            ? Math.Max(0f, Time.unscaledTime - _bivouacSuspendedSince)
            : 0f;
        var networkState = _network == null
            ? "network=null"
            : $"network=connected:{_network.IsConnected} handshake:{_network.IsHandshakeComplete} remotes:{_network.RemotePlayers.Count}";

        LoggerInstance.Msg(
            $"[BivouacDebug] phase={phase} elapsed={elapsed:0.0}s scene='{_currentScene ?? ""}' " +
            $"lastGameplay='{_lastGameplayScene ?? ""}' local={LocalState} suspended={_gameplaySyncSuspended} " +
            $"{DescribeRemoteStates()} " +
            $"panelVisible={_panel?.IsVisible == true} {networkState} {RemotePlayerManager.DebugSummary()} " +
            $"setFramePatchInstalled={NetplaySetFramePatch.IsNetplaySetFramePatchInstalled} " +
            $"patchPaused={_netplaySetFramePatchPausedForBivouac} " +
            BivouacDiagnostics.BuildBivouacDebugSnapshot());
    }

    /// <summary>
    /// Summarizes the lifecycle state of each known remote player. Used to diagnose
    /// the bivouac desync: if one side stays at Loading after exiting, the ghosts
    /// never reappear.
    /// </summary>
    private string DescribeRemoteStates()
    {
        if (_network == null)
            return "remoteStates=none";

        var summary = "remoteStates=[";
        var first = true;
        foreach (var kv in _network.RemotePlayers)
        {
            if (!first)
                summary += ",";
            first = false;
            var rp = kv.Value;
            summary += $"{kv.Key}:{(rp == null ? "null" : rp.State.ToString())}";
        }
        return summary + "]";
    }

    /// <summary>
    /// After leaving a bivouac, periodically logs the local + remote state + the ghost
    /// count to verify the sync recovers. Captures the case where both sides exit but
    /// no ghost respawns (deadlock).
    /// </summary>
    private void TickBivouacRecoveryDebug()
    {
        if (_bivouacRecoveryWatchUntil <= 0f)
            return;

        var now = Time.unscaledTime;
        if (now >= _bivouacRecoveryWatchUntil)
        {
            _bivouacRecoveryWatchUntil = 0f;
            LogBivouacDebug("recovery-end");
            return;
        }

        if (now < _nextBivouacRecoveryLogAt)
            return;

        _nextBivouacRecoveryLogAt = now + BivouacRecoveryLogIntervalSeconds;
        LogBivouacDebug("recovery");
    }

    // Pause/resume of the SetFrame patch during a bivouac via a simple flag (the patch
    // stays installed). We used to Uninstall/Install (UnpatchSelf/Patch) here, but that
    // Harmony churn fell within the bivouac's native save window and could break the
    // sealing/reopening of the save package -> 1 save OK then nothing.
    private void PauseNetplaySetFramePatchForBivouac()
    {
        // Cancel any deferred resume in progress (we re-enter a bivouac before the end
        // of the grace window) -> the patch must stay paused.
        _setFramePatchResumeAt = 0f;

        if (_netplaySetFramePatchPausedForBivouac)
            return;

        _netplaySetFramePatchPausedForBivouac = true;
        NetplaySetFramePatch.PauseSetFramePatch();
        LoggerInstance.Msg("[State] Netplay SetFrame patch paused for bivouac");
    }

    /// <summary>
    /// Resumes the SetFrame patch. <paramref name="immediate"/> = true for teardowns
    /// (disconnect, leave) where no save is in progress; false when leaving a bivouac,
    /// where we defer the resume (cf. <see cref="SetFramePatchResumeGraceSeconds"/>) to
    /// let the native save package seal before re-enabling injection.
    /// </summary>
    private void ResumeNetplaySetFramePatchAfterBivouac(bool immediate = true)
    {
        if (immediate)
        {
            _setFramePatchResumeAt = 0f;
            if (!_netplaySetFramePatchPausedForBivouac)
                return;

            _netplaySetFramePatchPausedForBivouac = false;
            NetplaySetFramePatch.ResumeSetFramePatch();
            LoggerInstance.Msg("[State] Netplay SetFrame patch resumed after bivouac");
            return;
        }

        if (!_netplaySetFramePatchPausedForBivouac)
            return;

        _setFramePatchResumeAt = Time.unscaledTime + SetFramePatchResumeGraceSeconds;
        LoggerInstance.Msg(
            $"[State] Netplay SetFrame patch resume scheduled in {SetFramePatchResumeGraceSeconds:F0}s (save-seal grace)");
    }

    /// <summary>Applies the deferred SetFrame patch resume scheduled when leaving a bivouac.</summary>
    private void UpdateDeferredSetFramePatchResume()
    {
        if (_setFramePatchResumeAt <= 0f)
            return;

        // Still in a bivouac / outside gameplay -> cancel the resume (we'll stay paused
        // until we're back in game in a stable way).
        if (_gameplaySyncSuspended || ShouldSuspendGameplaySync())
        {
            _setFramePatchResumeAt = 0f;
            return;
        }

        if (Time.unscaledTime < _setFramePatchResumeAt)
            return;

        _setFramePatchResumeAt = 0f;
        if (!_netplaySetFramePatchPausedForBivouac)
            return;

        _netplaySetFramePatchPausedForBivouac = false;
        NetplaySetFramePatch.ResumeSetFramePatch();
        LoggerInstance.Msg("[State] Netplay SetFrame patch resumed after bivouac (save-seal grace elapsed)");
    }

    private void SetLocalState(PlayerState state)
    {
        if (state == LocalState)
            return;

        LoggerInstance.Msg($"[State] local: {LocalState} -> {state}");
        LocalState = state;
    }

    public override void OnGUI()
    {
        _panel.OnGUI();
        PingMarkerManager.OnGUI();
        _chat?.OnGUI();
    }

    private void OnStartRequested()
    {
        if (StartGame.HasPending)
        {
            _panel.SetStatus("Launch already in progress.", true);
            return;
        }
        if (_lobby == null || !_lobby.IsInLobby)
        {
            _panel.SetStatus("Not connected to a lobby.", false);
            return;
        }
        if (!_lobby.IsHost)
        {
            _panel.SetStatus("Only the host can start.", true);
            return;
        }

        _panel.SetStatus("Starting lobby...", true);
        _lobby.BroadcastStart(BuildDefaultStartGame());
    }

    private static ServerStartGame BuildDefaultStartGame() => new()
    {
        Difficulty = (int)GameDifficulty.Explorer,
        SkipTutorials = true,
        SkipPractice = true,
        AssistEnabled = false,
    };


    private async void OnHostRequested(HostConfig cfg)
    {
        var playerName = GetSteamPlayerName();
        cfg = new HostConfig
        {
            PlayerName = playerName,
            LobbyName = cfg.LobbyName,
            MaxPlayers = cfg.MaxPlayers,
            Visibility = cfg.Visibility,
        };

        LoggerInstance.Msg($"Creating Steam lobby '{cfg.LobbyName}' as '{cfg.PlayerName}' " +
                           $"(max {cfg.MaxPlayers}, {cfg.Visibility})...");
        ModConfig.PlayerName.Value = cfg.PlayerName;
        ModConfig.MaxPlayers.Value = cfg.MaxPlayers;
        MelonPreferences.Save();

        _panel.SetCurrentLobbyName(cfg.LobbyName);
        BeginConnecting("Creating lobby");

        try
        {
            var ok = await _lobby.CreateLobby(cfg);
            if (!ok)
            {
                _connectingStart = null;
                if (string.IsNullOrEmpty(_lastLobbyError))
                    _panel.SetStatus("Failed to create lobby.", false);
            }
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"[CairnMP] CreateLobby failed: {ex}");
            _connectingStart = null;
            _panel.SetStatus($"Failed: {ex.Message}", false);
        }
    }

    /// <summary>Browser: calls SteamMatchmaking.RequestLobbyList and pushes the result to the UI.</summary>
    private async void OnBrowseRequested()
    {
        LoggerInstance.Msg("[Browse] Requesting Steam lobby list...");
        try
        {
            var lobbies = await _lobby.RequestLobbyList();
            _panel.SetBrowserLobbies(lobbies);
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"[Browse] Failed: {ex}");
            _panel.SetBrowserLobbies(System.Array.Empty<LobbyEntry>());
        }
    }

    /// <summary>Join via SteamID64 (browser or Steam invite).</summary>
    private async void OnJoinByLobbyIdRequested(ulong lobbyId)
    {
        LoggerInstance.Msg($"[Browse] Joining lobby {lobbyId}...");
        BeginConnecting("Joining lobby");
        try
        {
            var ok = await _lobby.JoinById(lobbyId);
            if (!ok)
            {
                _connectingStart = null;
                if (string.IsNullOrEmpty(_lastLobbyError))
                    _panel.SetStatus("Failed to join lobby.", false);
            }
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"[CairnMP] JoinById failed: {ex}");
            _connectingStart = null;
            _panel.SetStatus($"Failed: {ex.Message}", false);
        }
    }

    private async void OnJoinByCodeRequested(string playerName, string roomCode)
    {
        playerName = GetSteamPlayerName();
        LoggerInstance.Msg($"Joining lobby '{roomCode}' as '{playerName}'...");
        ModConfig.PlayerName.Value = playerName;

        BeginConnecting($"Joining {roomCode}");

        try
        {
            var ok = await _lobby.JoinByCode(roomCode);
            if (!ok)
            {
                _connectingStart = null;
                if (string.IsNullOrEmpty(_lastLobbyError))
                    _panel.SetStatus($"Lobby {roomCode} not found.", false);
            }
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"[CairnMP] JoinByCode failed: {ex}");
            _connectingStart = null;
            _panel.SetStatus($"Failed: {ex.Message}", false);
        }
    }

    /// <summary>Starts progress tracking and sets the first status.</summary>
    private void BeginConnecting(string verb)
    {
        _connectingVerb  = verb;
        _connectingStart = DateTime.UtcNow;
        _lastLobbyError  = null;
        _panel.SetConnecting($"{verb}...");
    }

    private void OnDisconnectRequested()
    {
        _lobby?.Leave();
        _network.Disconnect();
        WeatherApi.ResetRemoteWeatherSyncState();
        RemotePlayerManager.ClearAll();
        PingMarkerManager.ClearAll();
        _gameplaySyncSuspended = false;
        ResumeNetplaySetFramePatchAfterBivouac();
        _panel.SetStatus("Disconnected", false);
        LoggerInstance.Msg("Left lobby.");
    }

    private string GetSteamPlayerName()
    {
        var name = _lobby?.LocalPersonaName;
        if (!string.IsNullOrWhiteSpace(name))
            return name.Trim();

        return string.IsNullOrWhiteSpace(ModConfig.PlayerName?.Value)
            ? "Player"
            : ModConfig.PlayerName.Value.Trim();
    }

    /// <summary>Adapts the displayed status while we wait for the Steam round-trip for
    /// lobby creation / join. No server provisioning here — the callback is typically
    /// &lt; 1 s — so short messages only.</summary>
    private void TickConnectingStatus()
    {
        if (!_connectingStart.HasValue) return;
        // The OnLobbyEntered event will clear _connectingStart and write "Connected".
        if (_lobby != null && _lobby.IsInLobby) { _connectingStart = null; return; }

        var elapsed = (DateTime.UtcNow - _connectingStart.Value).TotalSeconds;
        string msg = elapsed switch
        {
            < 3  => $"{_connectingVerb}...",
            < 10 => $"{_connectingVerb} — waiting for Steam...",
            _    => $"{_connectingVerb} — taking longer than usual...",
        };
        _panel.SetConnecting(msg);
    }

    public override void OnDeinitializeMelon()
    {
        NetplaySetFramePatch.UninstallNetplaySetFramePatch();
        _lobby?.Dispose();
        _network?.Dispose();
        LoggerInstance.Msg("Cairn Multiplayer Mod unloaded.");
    }
}
