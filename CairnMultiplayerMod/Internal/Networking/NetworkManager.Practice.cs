using System;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Internal.Networking;

internal sealed partial class NetworkManager
{
    internal bool CanHostPracticePartner
        => IsHandshakeComplete && IsConnected && _steamLobby != null && _steamLobby.IsHost;

    /// <summary>
    /// Inserts the practice climber into the local remote list. He is not given a Steam id,
    /// so a lobby refresh does not remove him and no player state is broadcast for him.
    /// </summary>
    internal bool TrySpawnPracticePartner(float x, float y, float z, float yaw, string sceneName)
    {
        if (!CanHostPracticePartner || !PracticePartnerMotion.Finite(x, y, z) || !float.IsFinite(yaw))
            return false;
        if (LocalPlayerId == PracticePartner.PlayerId || _steamIdsByPlayerId.ContainsKey(PracticePartner.PlayerId))
            return false;

        var now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        if (!_remotePlayers.TryGetValue(PracticePartner.PlayerId, out var player) || player == null)
        {
            player = new RemotePlayer { Id = PracticePartner.PlayerId, Name = PracticePartner.Name };
            _remotePlayers[PracticePartner.PlayerId] = player;
            WritePracticePose(player, x, y, z, yaw, sceneName, now);
            OnPlayerJoined?.Invoke(PracticePartner.PlayerId, PracticePartner.Name);
            return true;
        }

        WritePracticePose(player, x, y, z, yaw, sceneName, now);
        return true;
    }

    internal bool TryMovePracticePartner(float x, float y, float z, float yaw, string sceneName)
    {
        if (!_remotePlayers.TryGetValue(PracticePartner.PlayerId, out var player) || player == null)
            return false;
        if (!PracticePartnerMotion.Finite(x, y, z) || !float.IsFinite(yaw)) return false;
        var now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        WritePracticePose(player, x, y, z, yaw, sceneName, now);
        // The frozen shape has to stay fresh or the body snaps back to a standing bind pose.
        if (player.HasPlayerFrame) player.LastPlayerFrameTime = now;
        return true;
    }

    /// <summary>
    /// Holds the shape of the body you just left. His climbot stays off: that child has
    /// no frame, and turning it on kills the runtime.
    /// </summary>
    internal bool TryHoldPracticeShape(NetFrameData frame)
    {
        if (!_remotePlayers.TryGetValue(PracticePartner.PlayerId, out var player) || player == null)
            return false;
        var usable = frame.IsValid && frame.Positions != null && frame.Positions.Length >= 3;
        player.HasPlayerFrame = usable;
        player.PlayerFrame = usable ? frame : default;
        player.HasClimbotFrame = false;
        player.ClimbotFrame = default;
        if (usable)
            player.LastPlayerFrameTime = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        return usable;
    }

    internal bool DespawnPracticePartner()
    {
        if (!_remotePlayers.Remove(PracticePartner.PlayerId)) return false;
        OnPlayerLeft?.Invoke(PracticePartner.PlayerId);
        return true;
    }

    private static void WritePracticePose(RemotePlayer player, float x, float y, float z, float yaw, string sceneName, double now)
    {
        player.X = x;
        player.Y = y;
        player.Z = z;
        player.YawDeg = yaw;
        player.SceneName = sceneName ?? "";
        player.State = PlayerState.InGame;
        player.LastUpdateTime = now;
        player.HasLinkPose = true;
        player.LinkX = x;
        player.LinkY = y;
        player.LinkZ = z;
    }
}
