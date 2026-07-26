using System;
using CairnMultiplayer.Shared;
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
            var succeeded = await _lobby.CreateLobby(config);
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

    /// <summary>Browser: calls SteamMatchmaking.RequestLobbyList and pushes the result to the UI.</summary>
    private async void OnBrowseRequested()
    {
        LoggerInstance.Msg("[Browse] Requesting Steam lobby list...");
        try
        {
            var lobbies = await _lobby.RequestLobbyList();
            _panel.SetBrowserLobbies(lobbies);
        }
        catch (Exception exception)
        {
            LoggerInstance.Error($"[Browse] Failed: {exception}");
            _panel.SetBrowserLobbies(Array.Empty<LobbyEntry>());
        }
    }

    /// <summary>Join via SteamID64 (browser or Steam invite).</summary>
    private async void OnJoinByLobbyIdRequested(ulong lobbyId)
    {
        LoggerInstance.Msg($"[Browse] Joining lobby {lobbyId}...");
        BeginConnecting("Joining lobby");
        try
        {
            var succeeded = await _lobby.JoinById(lobbyId);
            if (!succeeded)
                FinishFailedConnection("Failed to join lobby.");
        }
        catch (Exception exception)
        {
            ReportLobbyException("JoinById", exception);
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
            var succeeded = await _lobby.JoinByCode(roomCode);
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

    /// <summary>Starts progress tracking and sets the first status.</summary>
    private void BeginConnecting(string verb)
    {
        _connectingVerb = verb;
        _connectingStart = DateTime.UtcNow;
        _lastLobbyError = null;
        _panel.SetConnecting($"{verb}...");
    }

    private void OnDisconnectRequested()
    {
        _lobby?.Leave();
        _network.Disconnect();
        WeatherApi.ResetRemoteState();
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

}
