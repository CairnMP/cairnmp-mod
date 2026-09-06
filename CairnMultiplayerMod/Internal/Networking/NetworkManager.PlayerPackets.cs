using System;
using System.IO;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Internal.Networking;

/// <summary>Applies replicated player state and native animation frames.</summary>
internal sealed partial class NetworkManager
{
    private void HandlePlayerState(BinaryReader reader)
    {
        var packet = new ServerPlayerState();
        packet.Deserialize(reader);
        if (!PacketValidation.IsValidPose(packet.X, packet.Y, packet.Z, packet.YawDeg)) return;

        if (!_remotePlayers.TryGetValue(packet.PlayerId, out var player))
        {
            player = new RemotePlayer { Id = packet.PlayerId, Name = $"Player{packet.PlayerId}" };
            _remotePlayers[packet.PlayerId] = player;
        }

        player.X = packet.X;
        player.Y = packet.Y;
        player.Z = packet.Z;
        player.YawDeg = packet.YawDeg;
        player.SceneName = packet.SceneName;
        player.State = packet.State;
        player.LastUpdateTime = NowSeconds();
    }

    private void HandleBoneState(BinaryReader reader)
    {
        var packet = new ServerBoneState();
        packet.Deserialize(reader);
        if (!PacketValidation.IsValidBoneState(packet.BoneCount, packet.Positions, packet.Rotations)) return;
        if (!_remotePlayers.TryGetValue(packet.PlayerId, out var player)) return;

        player.BoneCount = packet.BoneCount;
        player.BonePositions = packet.Positions;
        player.BoneRotations = packet.Rotations;
    }

    private void HandlePlayerFrame(BinaryReader reader)
    {
        var packet = new ServerPlayerFrame();
        packet.Deserialize(reader);
        ApplyRemotePlayerFrame(packet.PlayerId, packet.PlayerName, packet.Frame);
    }

    private void HandleClimbotFrame(BinaryReader reader)
    {
        var packet = new ServerClimbotFrame();
        packet.Deserialize(reader);
        ApplyRemoteClimbotFrame(packet.PlayerId, packet.Frame);
    }

    private void ApplyRemotePlayerFrame(int playerId, string playerName, NetFrameData frame)
    {
        if (playerId == LocalPlayerId) return;
        if (!PacketValidation.IsValidNetFrame(frame, requirePosition: true, out var rejectReason))
        {
            LogRejectedNetFrame($"server-player-{playerId}", $"remote player id={playerId}", rejectReason);
            return;
        }

        if (!_remotePlayers.TryGetValue(playerId, out var player))
        {
            player = new RemotePlayer
            {
                Id = playerId,
                Name = string.IsNullOrWhiteSpace(playerName) ? $"Player{playerId}" : playerName,
            };
            _remotePlayers[playerId] = player;
            OnPlayerJoined?.Invoke(playerId, player.Name);
        }
        else if (!string.IsNullOrWhiteSpace(playerName))
        {
            player.Name = playerName;
        }

        player.HasPlayerFrame = frame.IsValid && frame.Positions != null && frame.Positions.Length >= 3;
        player.PlayerFrame = frame;
        player.State = player.HasPlayerFrame ? PlayerState.InGame : player.State;
        if (!player.HasPlayerFrame) return;

        player.X = frame.Positions[0];
        player.Y = frame.Positions[1];
        player.Z = frame.Positions[2];
        var now = NowSeconds();
        player.LastUpdateTime = now;
        player.LastPlayerFrameTime = now;
        LogRemotePlayerFrameAccepted(playerId, player.Name, frame);
    }

    private void ApplyRemoteClimbotFrame(int playerId, NetFrameData frame)
    {
        if (playerId == LocalPlayerId) return;
        if (!PacketValidation.IsValidNetFrame(frame, requirePosition: true, out var rejectReason))
        {
            LogRejectedNetFrame($"server-climbot-{playerId}", $"remote climbot id={playerId}", rejectReason);
            return;
        }
        if (!_remotePlayers.TryGetValue(playerId, out var player)) return;

        player.HasClimbotFrame = frame.IsValid && frame.Positions != null && frame.Positions.Length >= 3;
        player.ClimbotFrame = frame;
        if (player.HasClimbotFrame)
            LogRemoteClimbotFrameAccepted(playerId, frame);
    }
}
