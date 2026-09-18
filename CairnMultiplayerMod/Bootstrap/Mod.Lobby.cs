using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal;
using CairnMultiplayerMod.Internal.Game;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayerMod.Internal.Game.World;
using CairnMultiplayerMod.Internal.Networking;
using MelonLoader;

namespace CairnMultiplayerMod.Bootstrap;

public partial class Mod
{
    private void OnStartRequested()
    {
        if (StartGame.HasPending)
        {
            _panel.SetStatus("Launch already in progress.", true);
            return;
        }
        if (!Lobby.IsInLobby)
        {
            _panel.SetStatus("Not connected to a lobby.", false);
            return;
        }
        if (!Lobby.IsHost)
        {
            _panel.SetStatus("Only the host can start.", true);
            return;
        }

        var rules = Lobby.CurrentModeRules;
        _panel.SetStatus($"Starting {rules.Name}...", true);
        Lobby.BroadcastStart(BuildStartGame(rules));
    }

    /// <summary>The mode decides the difficulty; the rest are launch conveniences that have
    /// always been the same for every lobby.</summary>
    private static ServerStartGame BuildStartGame(MultiplayerModeRules rules) => new()
    {
        Difficulty = (int)rules.Difficulty,
        SkipTutorials = true,
        SkipPractice = true,
        AssistEnabled = false,
        Mode = (byte)rules.Mode,
        ExtraConstraints = (int)rules.ExtraConstraints,
    };

    private async void OnHostRequested(HostConfig config)
    {
        config = WithLocalSteamIdentity(config);
        LoggerInstance.Msg($"Creating Steam lobby '{config.LobbyName}' as '{config.PlayerName}' " +
                           $"(max {config.MaxPlayers}, {config.Visibility})...");
        ModConfig.PlayerName.Value = config.PlayerName;
        ModConfig.MaxPlayers.Value = config.MaxPlayers;
        MelonPreferences.Save();

        _panel.SetCurrentLobbyName(config.LobbyName);
        BeginConnecting("Creating lobby");

        try
        {
            var succeeded = await Lobby.CreateLobby(config);
            if (!succeeded)
                FinishFailedConnection("Failed to create lobby.");
        }
        catch (Exception exception)
        {
            ReportLobbyException("CreateLobby", exception);
        }
    }

    private HostConfig WithLocalSteamIdentity(HostConfig config) => new()
    {
        PlayerName = GetSteamPlayerName(),
        LobbyName = config.LobbyName,
        MaxPlayers = config.MaxPlayers,
        Visibility = config.Visibility,
    };

    private Task<List<LobbyEntry>> _browserRequest;

    private void OnBrowseRequested()
    {
        if (_browserRequest != null) return;
        LoggerInstance.Msg("[Browse] Requesting Steam lobby list...");
        try
        {
            _browserRequest = Lobby.RequestLobbyList();
        }
        catch (Exception exception)
        {
            LoggerInstance.Error($"[Browse] Failed: {exception}");
            _panel.SetBrowserLobbies(Array.Empty<LobbyEntry>());
        }
    }

    private void CompleteBrowserRequest()
    {
        if (_browserRequest?.IsCompleted != true) return;
        var request = _browserRequest;
        _browserRequest = null;
        // MelonLoader need not provide a Unity SynchronizationContext. An await
        // continuation can run on a worker and crash native UI creation/destruction.
        // Consume the result from OnUpdate, which always owns the Unity thread.
        try { _panel.SetBrowserLobbies(request.GetAwaiter().GetResult()); }
        catch (Exception exception)
        {
            LoggerInstance.Error($"[Browse] Failed: {exception}");
            _panel.SetBrowserLobbies(Array.Empty<LobbyEntry>());
        }
    }

    private async void OnJoinByLobbyIdRequested(ulong lobbyId)
    {
        LoggerInstance.Msg($"[Browse] Joining lobby {lobbyId}...");
        BeginConnecting("Joining lobby");
        try
        {
            var succeeded = await Lobby.JoinById(lobbyId);
            if (!succeeded)
                FinishFailedConnection("Failed to join lobby.");
        }
        catch (Exception exception)
        {
            ReportLobbyException("JoinById", exception);
        }
    }

    private async void OnJoinByCodeRequested(string roomCode)
    {
        string playerName = GetSteamPlayerName();
        LoggerInstance.Msg($"Joining lobby '{roomCode}' as '{playerName}'...");
        ModConfig.PlayerName.Value = playerName;
        BeginConnecting($"Joining {roomCode}");

        try
        {
            var succeeded = await Lobby.JoinByCode(roomCode);
            if (!succeeded)
                FinishFailedConnection($"Lobby {roomCode} not found.");
        }
        catch (Exception exception)
        {
            ReportLobbyException("JoinByCode", exception);
        }
    }

    private void FinishFailedConnection(string fallbackMessage)
    {
        _connectingStart = null;
        if (string.IsNullOrEmpty(_lastLobbyError))
            _panel.SetStatus(fallbackMessage, false);
    }

    private void ReportLobbyException(string operation, Exception exception)
    {
        LoggerInstance.Error($"[CairnMP] {operation} failed: {exception}");
        _connectingStart = null;
        _panel.SetStatus($"Failed: {exception.Message}", false);
    }

    private void BeginConnecting(string verb)
    {
        _connectingVerb = verb;
        _connectingStart = DateTime.UtcNow;
        _lastLobbyError = null;
        _panel.SetConnecting($"{verb}...");
    }

    private void OnDisconnectRequested()
    {
        SetMultiplayerModeActive(false);
        StartGame.Cancel();
        Lobby.Leave();
        Network.Disconnect();
        WeatherInterop.ResetRemoteState();
        RemotePlayerManager.ClearAll();
        PingMarkerManager.ClearAll();
        Bivouac.ForceResume();
        _panel.SetStatus("Disconnected", false);
        LoggerInstance.Msg("Left lobby.");
    }

    /// <summary>The native Story choice is an explicit opt-out from CairnMP gameplay.
    /// Leave any lobby before Cairn opens the solo save flow, and keep every feature
    /// dormant until a new multiplayer handshake succeeds.</summary>
    private void OnStoryModeSelected()
    {
        var wasInMultiplayer = _multiplayerModeActive || Lobby.IsInLobby || Network.IsConnected;
        OnDisconnectRequested();
        _panel.Hide();
        _mainMenu.RestoreNativeMenu();
        if (wasInMultiplayer)
            LoggerInstance.Msg("[CairnMP] Story selected — multiplayer features disabled for vanilla play.");
    }

    private void SetMultiplayerModeActive(bool active)
    {
        if (_multiplayerModeActive == active) return;

        _multiplayerModeActive = active;
        if (active)
        {
            Features?.NotifySessionStarted();
            return;
        }

        Features?.NotifySessionEnded();
        _hud?.Clear();
        PingMarkerManager.ClearAll();
    }

    private string GetSteamPlayerName()
    {
        var name = Lobby.LocalPersonaName;
        if (!string.IsNullOrWhiteSpace(name))
            return name.Trim();

        return string.IsNullOrWhiteSpace(ModConfig.PlayerName?.Value)
            ? "Player"
            : ModConfig.PlayerName.Value.Trim();
    }

}
