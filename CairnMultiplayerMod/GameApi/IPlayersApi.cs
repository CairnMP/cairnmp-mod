using System.Collections.Generic;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.GameApi;

/// <summary>Safe capture and application operations for local and remote climbers.</summary>
internal interface IPlayersApi
{
    IReadOnlyList<int> RemotePlayersInGame { get; }
    IReadOnlyList<int> RemoteSleepParticipants { get; }

    bool TryGetLocation(int playerId, out PlayerLocation location);

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

    /// <summary>
    /// Shakes the local climber's holds loose, the way the game does when a roped partner
    /// falls. A firm hold resists it. Returns whether anything actually let go.
    /// </summary>
    bool ShakeLocalClimberGrip();
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
