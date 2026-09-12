using System;
using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game;
using CairnMultiplayerMod.Internal.Game.Bivouac;
using CairnMultiplayerMod.Internal.Game.MainMenu;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayerMod.Internal.Game.Roping;
using CairnMultiplayerMod.Internal.Game.World;
using CairnMultiplayerMod.Internal.Game.Voice;
using CairnMultiplayerMod.Internal.Networking;
using CairnMultiplayerMod.Internal.UI;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(CairnMultiplayerMod.Bootstrap.Mod), "Cairn Multiplayer Mod", "2.2.4", "CairnModTeam")]
[assembly: MelonGame("TheGameBakers", "Cairn")]

namespace CairnMultiplayerMod.Bootstrap;

public partial class Mod : MelonMod
{
    /// <summary>Verbose diagnostic logs (scenes, native object resolution, etc.). OFF by
    /// default to keep the console clean; set to true to debug.</summary>
    private static bool VerboseLogging { get; set; }

    /// <summary>Diagnostic log: writes ONLY if VerboseLogging is enabled.</summary>
    private void LogDebug(string message)
    {
        if (VerboseLogging) LoggerInstance.Msg(message);
    }

    private IMultiplayerPanel _panel;
    private MainMenuAdapter _mainMenu;
    private HudAdapter _hud;
    private IGameApi _game;
    private VoiceAdapter _voice;
    private InventoryAdapter _inventory;
    private IGameRegistration _mainMenuButton;
    private readonly HashSet<string> _loadedScenes = new(StringComparer.Ordinal);
    private Key _connectKey;
    private Key _disconnectKey;
    private bool _runtimeStopped;
    private bool _quitIssued;
    private bool _multiplayerModeActive;

    // Tracks the progress of lobby creation/join so the status message can evolve while
    // we wait on the Steam round-trip (cf. TickConnectingStatus).
    private DateTime? _connectingStart;
    private string _connectingVerb = "Creating lobby"; // "Creating lobby" | "Joining lobby"
    private string _lastLobbyError;

    internal NetworkManager Network { get; private set; }
    internal SteamLobbyManager Lobby { get; private set; }
    private readonly RuntimeState _runtimeState = new();
    internal PlayerState LocalState => _runtimeState.LocalPlayerState;

    // Per-feature sync components, constructed in OnInitializeMelon and ticked from OnUpdate.
    private PlayerStateBroadcaster Player { get; set; }
    internal RopeCoupleController Rope { get; private set; }
    private StartGameFlow StartGame { get; set; }
    private BivouacSyncGate Bivouac { get; set; }

    /// <summary>Runs the self-registering features (see Framework/). Mod knows nothing about
    /// them individually — adding one never touches this file.</summary>
    private FeatureHost Features { get; set; }

    // Scene state read by the sync components (kept authoritative here, on the mod core).
    internal string CurrentScene => _runtimeState.CurrentScene;
    internal string LastGameplayScene => _runtimeState.LastGameplayScene;

    public override void OnInitializeMelon()
    {
        CrashHandler.Initialize(
            message => LoggerInstance.Msg(message),
            message => LoggerInstance.Warning(message),
            message => LoggerInstance.Error(message));

        try
        {
            Il2CppExceptionCapture.Install();
            ModConfig.Register();
            VoicePreferences.Register();
            VerboseLogging = ModConfig.VerboseLogging.Value;
            ModLog.Initialize(
                message => LoggerInstance.Msg(message),
                message => LoggerInstance.Warning(message),
                message => LoggerInstance.Error(message),
                LogDebug);
            FeatureLog.SetSink(
                message => LoggerInstance.Msg(message),
                message => LoggerInstance.Warning(message),
                message => LoggerInstance.Error(message));

            InstallGamePatches();
            CreateComponents();
            WireLobbyEvents();
            WirePanelEvents();
            WireNetworkEvents();
            RegisterFeatures();
            if (CrashHandler.IsFatal) return;

            LoggerInstance.Msg("===========================================");
            LoggerInstance.Msg($"  Cairn Multiplayer Mod v{Protocol.GameVersion} loaded!");
            LoggerInstance.Msg("  Diagnostics stay local unless you share them.");
            LoggerInstance.Msg("===========================================");
        }
        catch (Exception exception)
        {
            CrashHandler.Fatal(exception, "Mod.OnInitializeMelon");
        }
    }

    /// <summary>
    /// Hands the generated feature list to the host. Must happen before any lobby is joined:
    /// the features declare their network contracts here, and those go into the manifest
    /// peers negotiate on connection.
    /// </summary>
    private void RegisterFeatures()
    {
        Features = new FeatureHost(
            game: _game,
            networkProvider: () => Network,
            isActive: () => _multiplayerModeActive);
        try
        {
            Features.RegisterAll(FeatureRegistry.CreateAll(), new Version(Protocol.GameVersion));
        }
        catch (Exception ex)
        {
            // A feature that cannot declare itself is a build-time mistake that slipped
            // through — say so loudly, the mod would otherwise run with a silent hole.
            LoggerInstance.Error($"[Features] Registration failed: {ex}");
            CrashHandler.Fatal(ex, "Mod.RegisterFeatures");
        }
    }

    private void InstallGamePatches()
    {
        MultiplayerPausePatch.Configure(() => Network?.IsConnected == true);
        GamePatchRegistry.InstallAll();
    }

    private void CreateComponents()
    {
        _connectKey = ParseKey(ModConfig.ConnectKey.Value, Key.F5);
        _disconnectKey = ParseKey(ModConfig.DisconnectKey.Value, Key.F6);
        Network = new NetworkManager(RopeLinkState.Links);
        Lobby = new SteamLobbyManager();
        _panel = MultiplayerPanelFactory.Create(Lobby);
        _mainMenu = new MainMenuAdapter(OnStoryModeSelected);
        _hud = new HudAdapter();
        _voice = new VoiceAdapter(() => Network, () => LocalState == PlayerState.InGame);
        _inventory = new InventoryAdapter();
        _game = new GameApiFacade(
            _mainMenu,
            new GameStateAdapter(() => LocalState),
            new GameTimeAdapter(),
            new GameInputAdapter(),
            _hud,
            new ChatAdapter(() => Network),
            _inventory,
            new ClockAdapter(),
            new PlayersAdapter(() => Network, _runtimeState),
            new WeatherAdapter(),
            new WorldAdapter(), _voice);

        // Per-feature sync components share only their explicit dependencies and are
        // ticked, in this exact order, from OnUpdate.
        Rope = new RopeCoupleController(Network, _runtimeState);
        Player = new PlayerStateBroadcaster(
            Network, _runtimeState, Rope.Reset, () => Bivouac?.BlocksGameplaySync() == true);
        StartGame = new StartGameFlow(_panel, _mainMenu.RestoreMainMenuInput, _runtimeState);
        Bivouac = new BivouacSyncGate(
            Network, _panel, _runtimeState, ResetSyncTimers, ResetSceneBoundSyncState, SetLocalState);
    }

    /// <summary>
    /// Steam Matchmaking → UI wiring. The Steam callbacks are pumped by Cairn itself on
    /// the Unity thread, so there's no marshalling to do.
    /// </summary>
    private void WireLobbyEvents()
    {
        Lobby.OnLobbyEntered += _ =>
        {
            _connectingStart = null;
            _lastLobbyError = null;
            var code = Lobby.CurrentRoomCode;
            _panel.SetCurrentLobbyName(Lobby.CurrentLobbyName);
            _panel.SetStatus(string.IsNullOrEmpty(code) ? "Connected" : $"Connected — code {code}", true);
            Network.StartSteamTransport(Lobby);
        };
        Lobby.OnLobbyError += err =>
        {
            _connectingStart = null;
            _lastLobbyError = err;
            _panel.SetStatus($"Failed: {err}", Lobby.IsInLobby);
        };
        if (!Lobby.IsSteamIntegrationAvailable)
        {
            _lastLobbyError = Lobby.SteamUnavailableReason;
            _panel.SetStatus($"Unavailable: {_lastLobbyError}", false);
        }
        Lobby.OnLobbyLeft += () =>
        {
            SetMultiplayerModeActive(false);
            StartGame.Cancel();
            RopeInterop.ClearRemotePitons();
            Network.Disconnect();
            WeatherInterop.ResetRemoteState();
            RemotePlayerManager.ClearAll();
            Rope.ClearLinks();
            Bivouac.ForceResume();
            _panel.SetStatus("Disconnected", false);
        };
        Lobby.OnMembersChanged += () =>
        {
            // The panel rebuilds its member list on every Tick from _lobby.Members,
            // but we also push a short status so the footer reflects the event.
            if (!Lobby.IsInLobby) return;
            Network.RefreshSteamLobbyMembers(Lobby);
            _panel.SetStatus($"{Lobby.Members.Count} player(s) in lobby", true);
        };
        Lobby.OnStartRequested += StartGame.Begin;
    }

    private void WirePanelEvents()
    {
        // The safe API stores this logical registration; its internal adapter attaches it
        // whenever Cairn's scene-bound native menu becomes available.
        _mainMenuButton = _game.MainMenu.AddButton("multiplayer", "Multiplayer", _panel.Show);

        _panel.OnHostRequested += OnHostRequested;
        _panel.OnJoinByCodeRequested += OnJoinByCodeRequested;
        _panel.OnBrowseRequested += OnBrowseRequested;
        _panel.OnJoinByLobbyIdRequested += OnJoinByLobbyIdRequested;
        _panel.OnDisconnectRequested += OnDisconnectRequested;
        _panel.OnStartRequested += OnStartRequested;
        _panel.OnPanelClosed += _mainMenu.RestoreNativeMenu;
    }

    private void WireNetworkEvents()
    {
        Network.OnHandshakeAck += () =>
        {
            _panel.SetStatus($"Connected to {Network.ServerName} (id={Network.LocalPlayerId})", true);
            SetMultiplayerModeActive(true);
        };
        Network.OnFeatureStream += (fromPlayerId, channel, payload) =>
            Features.DispatchStream(fromPlayerId, channel, payload);
        Network.OnHandshakeRejected += reason =>
            _panel.SetStatus($"Rejected: {reason}", false);
        Network.OnDisconnected += _ =>
        {
            SetMultiplayerModeActive(false);
            StartGame.Cancel();
            RopeInterop.ClearRemotePitons();
            WeatherInterop.ResetRemoteState();
            Rope.ClearLinks();
            Bivouac.ForceResume();
        };
        Network.OnPlayerJoined += (id, name) =>
        {
            _panel.SetStatus($"Player joined: {name}", Network.IsConnected);
            Network.RemotePlayers.TryGetValue(id, out var rp);
            RemotePlayerManager.OnPlayerJoined(id, name, rp);
        };
        Network.OnPlayerLeft += id =>
        {
            _panel.SetStatus($"Player {id} left", Network.IsConnected);
            RemotePlayerManager.OnPlayerLeft(id);
            RopeLinkState.RemovePlayer(id);
        };
        // Roping: apply each authoritative clip/unclip to the global link state.
        Network.OnRopeClip += RopeLinkState.Apply;

        // Piton sync: spawn the pitons placed by other players via Lifeline.AddPiton.
        Network.OnPitonPlaced += pkt =>
        {
            if (IsGameplaySyncSuspended())
                return;

            RopeInterop.SpawnRemotePiton(pkt.PitonId,
                new Vector3(pkt.PosX, pkt.PosY, pkt.PosZ),
                new Quaternion(pkt.RotX, pkt.RotY, pkt.RotZ, pkt.RotW),
                pkt.Quality, pkt.PitonHp, pkt.ItemId);
        };
        Network.OnPitonRemoved += pkt =>
        {
            if (IsGameplaySyncSuspended())
                return;

            RopeInterop.RemoveRemotePiton(pkt.PitonId);
        };
        Network.OnTeleport += pkt =>
        {
            // The player may have entered a bivouac between the host check and delivery.
            if (GameLifecycleService.IsLocalInBivouac())
            {
                LogDebug("[CairnMP] ServerTeleport ignored (local player is in a bivouac)");
                return;
            }

            LogDebug($"[CairnMP] ServerTeleport received -> ({pkt.X:F1}, {pkt.Y:F1}, {pkt.Z:F1})");
            TeleportInterop.TeleportLocalPlayer(new Vector3(pkt.X, pkt.Y, pkt.Z), pkt.Yaw);
        };
        // Authoritative game launch by the server: queues the packet, applies it in
        // Update when the MainMenu scene is active.
        Network.OnStartGameReceived += pkt => StartGame.Begin(pkt);
    }
}
