using CairnMultiplayerMod.Internal.Diagnostics;
using System.IO;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Internal.Networking;

/// <summary>Applies connection, roster and session-control packets.</summary>
internal sealed partial class NetworkManager
{
    private void HandleHandshakeAck(BinaryReader reader)
    {
        var packet = new ServerHandshakeAck();
        packet.Deserialize(reader);
        LocalPlayerId = packet.AssignedPlayerId;
        ServerName = packet.ServerName;
        IsHandshakeComplete = true;
        ModLog.Info($"[CairnMP] Handshake OK. id={packet.AssignedPlayerId} server='{packet.ServerName}'");
        OnHandshakeAck?.Invoke();
    }

    private void HandleHandshakeReject(BinaryReader reader)
    {
        var packet = new ServerHandshakeReject();
        packet.Deserialize(reader);
        LastError = packet.Reason;
        ModLog.Error($"[CairnMP] Handshake rejected: {packet.Reason}");
        OnHandshakeRejected?.Invoke(packet.Reason);
    }

    private void HandlePlayerJoined(BinaryReader reader)
    {
        var packet = new ServerPlayerJoined();
        packet.Deserialize(reader);
        _remotePlayers[packet.PlayerId] = new RemotePlayer
        {
            Id = packet.PlayerId,
            Name = packet.PlayerName,
        };
        ModLog.Info($"[CairnMP] Player joined: [{packet.PlayerId}] {packet.PlayerName}");
        OnPlayerJoined?.Invoke(packet.PlayerId, packet.PlayerName);
    }

    private void HandlePlayerLeft(BinaryReader reader)
    {
        var packet = new ServerPlayerLeft();
        packet.Deserialize(reader);
        if (_remotePlayers.Remove(packet.PlayerId))
            ModLog.Info($"[CairnMP] Player left: {packet.PlayerId}");
        OnPlayerLeft?.Invoke(packet.PlayerId);
    }

    private void HandleStartGame(BinaryReader reader)
    {
        var packet = new ServerStartGame();
        packet.Deserialize(reader);
        ModLog.Debug($"[CairnMP] StartGame: difficulty={(GameDifficulty)packet.Difficulty} skipTut={packet.SkipTutorials} skipPra={packet.SkipPractice} assist={packet.AssistEnabled}");
        OnStartGameReceived?.Invoke(packet);
    }
}
