using System.Collections.Generic;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.GameApi;

/// <summary>Safe capture and application operations for local and remote climbers.</summary>
internal interface IPlayersApi
{
    IReadOnlyList<int> RemotePlayersInGame { get; }
    IReadOnlyList<int> RemoteSleepParticipants { get; }

    /// <summary>
    /// Remote climbers still in the run: on the wall, camped in a bivouac, or between
    /// scenes. A zone load and a story scene both report as loading, and that climber can
    /// still reach a bivouac. A camper is missing from <see cref="RemotePlayersInGame"/> —
    /// gameplay sync stands down at camp — yet they are exactly the teammate who can call
    /// the fallen back. A climber back at the main menu is absent from this list.
    /// </summary>
    IReadOnlyList<int> RemotePlayersInGameOrBivouac { get; }

    /// <summary>True while that remote player is camped in a bivouac.</summary>
    bool IsRemotePlayerInBivouac(int playerId);

    bool TryGetLocation(int playerId, out PlayerLocation location);

    bool TryGetInteractionLocation(int playerId, out PlayerLocation location, out string reason)
    {
        var ready = TryGetLocation(playerId, out location);
        reason = ready ? "" : "The synchronized player position is not ready.";
        return ready;
    }

    bool TryCaptureHandPose(out byte[] packed);
    bool TryCaptureAppearance(out int packed);
    bool TryCaptureCosmetics(out byte flags);
    void SetRemoteHandPose(int playerId, byte[] packed);
    void SetRemoteAppearance(int playerId, int packed);
    void SetRemoteCosmetics(int playerId, byte flags);
    void ResetHandPoseCaches();
    void ResetAppearanceCaches();

    /// <summary>True while the local climber is off the wall and falling.</summary>
    bool IsLocalPlayerFalling { get; }

    /// <summary>True when that player and the local climber share a rope.</summary>
    bool IsRopedToLocalPlayer(int playerId);
}

internal readonly struct PlayerLocation
{
    public PlayerLocation(float x, float y, float z, string scene, PlayerState state)
    { X = x; Y = y; Z = z; Scene = scene ?? ""; State = state; }

    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public string Scene { get; }
    public PlayerState State { get; }
}
