using System;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Players;

namespace CairnMultiplayerMod.Internal.Game.Life;

/// <summary>Binds the spectator camera to the players a session knows about.</summary>
internal sealed class SpectatorAdapter : ISpectatorApi
{
    private int _followedPlayerId = -1;

    public bool IsActive => SpectatorCamera.IsActive;
    public bool IsFollowing => SpectatorCamera.IsFollowing;
    public int FollowedPlayerId => SpectatorCamera.IsFollowing ? _followedPlayerId : -1;

    public bool Enter() => SpectatorCamera.Enter();

    public void Leave()
    {
        _followedPlayerId = -1;
        SpectatorCamera.Leave();
    }

    public void FreeLook()
    {
        _followedPlayerId = -1;
        SpectatorCamera.FreeLook();
    }

    public bool Follow(int playerId)
    {
        try
        {
            if (!RemotePlayerManager.TryGetGhostTransform(playerId, out var ghost)) return false;
            if (!SpectatorCamera.Follow(ghost)) return false;
            _followedPlayerId = playerId;
            return true;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("spectator.follow", exception);
            return false;
        }
    }

    public void Tick(bool acceptInput)
    {
        SpectatorCamera.Tick(acceptInput);
        // The camera drops a target that was destroyed under it; keep our own idea of who is
        // being watched in step, so the feature can pick someone else.
        if (!SpectatorCamera.IsFollowing) _followedPlayerId = -1;
    }
}
