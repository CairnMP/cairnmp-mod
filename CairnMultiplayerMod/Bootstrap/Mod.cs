using System;
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
    private string _currentScene;
    private readonly HashSet<string> _loadedScenes = new(StringComparer.Ordinal);
    private string _lastGameplayScene;
    private float _timeSinceLastSceneLoad;
    private Key _connectKey;
    private Key _disconnectKey;

    // Tracks the progress of lobby creation/join so the status message can evolve while
    // we wait on the Steam round-trip (cf. TickConnectingStatus).
    private DateTime? _connectingStart;
    private string    _connectingVerb = "Creating lobby"; // "Creating lobby" | "Joining lobby"
    private string    _lastLobbyError;

    public NetworkManager Network => _network;
    public SteamLobbyManager Lobby => _lobby;
    public PlayerState LocalState { get; private set; } = PlayerState.Unknown;

    // Per-feature sync components, constructed in OnInitializeMelon and ticked from OnUpdate.
    internal PlayerStateBroadcaster Player { get; private set; }
    internal RopeCoupleController Rope { get; private set; }
    internal StartGameFlow StartGame { get; private set; }
    internal BivouacSyncGate Bivouac { get; private set; }

    /// <summary>Runs the self-registering features (see Framework/). Mod knows nothing about
    /// them individually — adding one never touches this file.</summary>
    internal FeatureHost Features { get; private set; }

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

        InstallGamePatches();
        CreateComponents();
        WireLobbyEvents();
        WirePanelEvents();
        WireNetworkEvents();
        RegisterFeatures();

        LoggerInstance.Msg("===========================================");
        LoggerInstance.Msg($"  Cairn Multiplayer Mod v{Protocol.GameVersion} loaded!");
        LoggerInstance.Msg("===========================================");
    }

    /// <summary>
    /// Hands the generated feature list to the host. Must happen before any lobby is joined:
    /// the features declare their network contracts here, and those go into the manifest
    /// peers negotiate on connection.
    /// </summary>
    private void RegisterFeatures()
    {
        Features = new FeatureHost();
        try
        {
            Features.RegisterAll(FeatureRegistry.CreateAll(), new Version(Protocol.GameVersion));
        }
        catch (Exception ex)
        {
            // A feature that cannot declare itself is a build-time mistake that slipped
            // through — say so loudly, the mod would otherwise run with a silent hole.
            LoggerInstance.Error($"[Features] Registration failed: {ex}");
            CrashReporter.ReportCaughtExceptionOnce(ex, "Mod.RegisterFeatures");
        }
    }

    private static void InstallGamePatches()
    {
        NetplaySetFramePatch.Install();
        BivouacDiagnostics.Install();
        RopeTeamFallPatch.Install();
        MultiplayerPausePatch.Install();
        FreeRoamUnlockPatch.Install();
        SavegamePitonGuardPatch.Install();
    }

    private void CreateComponents()
    {
        _connectKey = ParseKey(ModConfig.ConnectKey.Value, Key.F5);
        _disconnectKey = ParseKey(ModConfig.DisconnectKey.Value, Key.F6);
        _network = new NetworkManager();
        _lobby   = new SteamLobbyManager();
        _panel = MultiplayerPanelFactory.Create();

        // Per-feature sync components. They read shared state via Mod.Instance and are
        // ticked, in this exact order, from OnUpdate.
        Rope = new RopeCoupleController(_network);
        Player = new PlayerStateBroadcaster(_network);
        StartGame = new StartGameFlow(_panel);
        Bivouac = new BivouacSyncGate(_network, _panel);
    }

    /// <summary>
    /// Steam Matchmaking → UI wiring. The Steam callbacks are pumped by Cairn itself on
    /// the Unity thread, so there's no marshalling to do.
    /// </summary>
    private void WireLobbyEvents()
    {
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
        if (!_lobby.IsSteamIntegrationAvailable)
        {
            _lastLobbyError = _lobby.SteamUnavailableReason;
            _panel.SetStatus($"Unavailable: {_lastLobbyError}", false);
        }
        _lobby.OnLobbyLeft += () =>
        {
            _network.Disconnect();
            WeatherApi.ResetRemoteState();
            RemotePlayerManager.ClearAll();
            Rope.ClearLinks();
            Bivouac.ForceResume();
            Features.NotifySessionEnded();
            _panel.SetStatus("Disconnected", false);
        };
        _lobby.OnMembersChanged += () =>
        {
            // The panel rebuilds its member list on every Tick from _lobby.Members,
            // but we also push a short status so the footer reflects the event.
            if (!_lobby.IsInLobby) return;
            _network.RefreshSteamLobbyMembers(_lobby);
            _panel.SetStatus($"{_lobby.Members.Count} player(s) in lobby", true);
        };
        _lobby.OnStartRequested += StartGame.Begin;
    }

    private void WirePanelEvents()
    {
        // Menu button: the Multiplayer click hides the Cairn menu and opens the panel.
        MainMenuMultiplayerButton.Bind(_panel);

        _panel.OnHostRequested            += OnHostRequested;
        _panel.OnJoinByCodeRequested      += OnJoinByCodeRequested;
        _panel.OnBrowseRequested          += OnBrowseRequested;
        _panel.OnJoinByLobbyIdRequested   += OnJoinByLobbyIdRequested;
        _panel.OnDisconnectRequested      += OnDisconnectRequested;
        _panel.OnStartRequested           += OnStartRequested;
        _panel.OnPanelClosed              += MainMenuMultiplayerButton.RestoreModeSelect;
    }

    private void WireNetworkEvents()
    {
        _network.OnHandshakeAck += () =>
        {
            _panel.SetStatus($"Connected to {_network.ServerName} (id={_network.LocalPlayerId})", true);
            Features.NotifySessionStarted();
        };
        _network.OnHandshakeRejected += reason =>
            _panel.SetStatus($"Rejected: {reason}", false);
        _network.OnDisconnected += _ =>
        {
            WeatherApi.ResetRemoteState();
            Rope.ClearLinks();
            Bivouac.ForceResume();
            Features.NotifySessionEnded();
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
        // Authoritative game launch by the server: queues the packet, applies it in
        // Update when the MainMenu scene is active.
        _network.OnStartGameReceived += pkt => StartGame.Begin(pkt);
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        _loadedScenes.Add(sceneName);
        if (SceneRoles.IsGameplayRoot(sceneName))
            _lastGameplayScene = sceneName;

        _currentScene = sceneName;
        _timeSinceLastSceneLoad = 0f;
        LogDebug($"Scene loaded: [{buildIndex}] {sceneName}");

        var isMainMenu = SceneRoles.IsMainMenu(sceneName);

        // FreeRoam unlock: active ONLY at the MainMenu (forcing the flag during boot
        // or in game sends the game onto an unready FreeRoam init path -> black screen).
        FreeRoamUnlockPatch.SetActive(isMainMenu);

        if (!isMainMenu)
            _panel.DestroyResources();

        if (SceneRoles.IsBivouac(sceneName))
        {
            LogDebug($"[State] Scene {sceneName} treated as Loading for multiplayer");
            Bivouac.LogPhase("scene-loaded");
        }

        // Note: we do NOT clear the pings here — they're positioned in the world and
        // expire on their own (15 s). Clearing them on every scene stream would make
        // them disappear while we're still in the area.
        if (SceneRoles.IsSyncResetPoint(sceneName))
        {
            ResetSceneBoundSyncState();
            RemotePlayerManager.ClearAll();
        }

        MainMenuMultiplayerButton.OnSceneLoaded(sceneName);

        if (!isMainMenu)
            return;

        var inLobby = _lobby?.IsInLobby == true;
        // Still connected but no lobby left to belong to: cut it here so the player can
        // reconnect cleanly instead of dragging a half-dead session around the menu.
        var strandedConnection = _network.IsConnected && !inLobby;
        if (strandedConnection)
        {
            LoggerInstance.Msg("[State] Returned to MainMenu — auto-disconnecting");
            _network.Disconnect();
            _panel.SetStatus("Disconnected (returned to menu)", false);
        }

        // The ghosts and rope links belong to the session we just left. Nothing to clear
        // if we were neither connected nor in a lobby.
        if (strandedConnection || inLobby)
        {
            RemotePlayerManager.ClearAll();
            Rope.ClearLinks();
        }
    }

    public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
    {
        _loadedScenes.Remove(sceneName);

        if (string.Equals(_currentScene, sceneName, StringComparison.Ordinal))
            _currentScene = ResolveCurrentSceneAfterUnload(sceneName);

        if (!SceneRoles.IsBivouac(sceneName))
            return;

        _timeSinceLastSceneLoad = 0f;
        LogDebug($"Scene unloaded: [{buildIndex}] {sceneName}");
        LogDebug($"[State] Scene {sceneName} overlay unloaded for multiplayer");
        ResetSceneBoundSyncState();
    }

    /// <summary>Forgets everything bound to the scene we're leaving: IL2CPP caches held by
    /// the game services, then the sync timers.</summary>
    internal void ResetSceneBoundSyncState()
    {
        SceneCache.Reset();
        ResetSyncTimers();
        Features?.NotifySceneReset();
    }

    /// <summary>Restarts the broadcast cadence from scratch, without touching the caches.
    /// Features rearm their own cadence through OnSceneReset.</summary>
    internal void ResetSyncTimers()
    {
        Player.ResetSyncState();
        Player.ResetTimers();
    }

    internal bool IsGameplaySyncSuspended() => Bivouac.BlocksGameplaySync();

    /// <summary>After an unload, the current scene becomes whichever gameplay root is
    /// still loaded — or the last one we knew, if the unloaded scene wasn't it.</summary>
    private string ResolveCurrentSceneAfterUnload(string unloadedScene)
    {
        foreach (var scene in _loadedScenes)
        {
            if (SceneRoles.IsGameplayRoot(scene))
                return scene;
        }

        if (!string.Equals(_lastGameplayScene, unloadedScene, StringComparison.Ordinal))
            return _lastGameplayScene;

        return null;
    }

    public override void OnUpdate()
    {
        _timeSinceLastSceneLoad += Time.unscaledDeltaTime;

        // PANIC failsafe (F10): force-unblock the game's inputs whatever the state. Read
        // from the raw keyboard device (never affected by the block) and placed BEFORE any
        // early return below, so it stays reachable even mid-bivouac. Whoever captured the
        // keyboard releases it on its own side — the chat feature also listens for F10.
        var panicKeyboard = Keyboard.current;
        if (panicKeyboard != null && panicKeyboard.f10Key.wasPressedThisFrame)
        {
            InputApi.ForceClearBlock();
            LoggerInstance.Msg("[CairnMP] Panic: input force-cleared (F10)");
        }

        // While a mod UI has the keyboard (chat), the mod's own shortcuts must not fire —
        // otherwise typing text triggers actions. The game itself is blocked at the
        // InputManager level. Together = total block.
        bool chatTyping = FeatureInput.KeyboardCaptured;

        // Update the button injection in the main menu
        if (SceneRoles.IsMainMenu(_currentScene))
        {
            MainMenuMultiplayerButton.OnUpdate();
            // Unlock FreeRoam: force the tweakable field as soon as it's loaded (no-op
            // once it succeeds). Complements the Harmony postfix on the public property.
            FreeRoamUnlockPatch.TryForceTweakableField();
            // Unhide the FreeRoam mode in the difficulty list (isHidden=false).
            FreeRoamUnlockPatch.TryUnhideDifficulty();
        }

        // Pump the managed Steam callback queue (also handles deferred init).
        _lobby?.Pump(Time.unscaledDeltaTime);

        // Lock the gameplay layer before processing network packets.
        Bivouac.Update();

        // Process network events
        _network.Update();

        // Features that must keep running whatever the state (input, HUD upkeep) — placed
        // before the bivouac early-return, like the other always-on ticks below.
        Features.Tick(FeaturePhase.Always);

        // Name toggle (N) — placed BEFORE the bivouac/photo suspension return so it stays
        // reachable in photo mode (where gameplay is suspended).
        if (!chatTyping) TickNameToggleInput();

        // Injection of the "N" row into the native photo-mode legend. The clone is
        // instantiated under an inactive parent then stripped of its non-visual components
        // (otherwise the duplicated input handlers block the game); injected only when photo
        // mode is actually open.
        try { PhotoModeNamesRow.Tick(); }
        catch (Exception ex) { LoggerInstance.Error($"[PhotoNames] tick failed: {ex.Message}"); }

        // During a bivouac, Cairn itself drives the pawn, the camera and the taping
        // hands. The mod only keeps a minimal network presence.
        if (Bivouac.IsSuspended)
        {
            Bivouac.TickSuspendedLog();
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
        Bivouac.TickRecoveryLog();

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

        // Features that touch the world — only once gameplay sync is active.
        Features.Tick(FeaturePhase.Gameplay);

        // Progressive waiting status during lobby creation/join (provisioning).
        TickConnectingStatus();

        // Long-distance post-teleport settle (prevents falling into the void + fixes the
        // zone-load repositioning). No-op if no teleport is pending.
        TeleportApi.TickSettle(LocalState == PlayerState.InGame);

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
                if (!SceneRoles.IsMainMenu(_currentScene))
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

    internal void SetLocalState(PlayerState state)
    {
        if (state == LocalState)
            return;

        LoggerInstance.Msg($"[State] local: {LocalState} -> {state}");
        LocalState = state;
    }

    public override void OnGUI()
    {
        _panel.OnGUI();
        Features?.DrawHud();
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
        NetplaySetFramePatch.Uninstall();
        _lobby?.Dispose();
        _network?.Dispose();
        LoggerInstance.Msg("Cairn Multiplayer Mod unloaded.");
    }
}
