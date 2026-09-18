using System.Collections.Generic;

namespace CairnMultiplayerMod.GameApi;

internal readonly struct WorldPosition
{
    internal WorldPosition(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public float X { get; }
    public float Y { get; }
    public float Z { get; }
}

/// <summary>World queries and transient multiplayer markers.</summary>
internal interface IWorldApi
{
    bool IsFreeCameraActive { get; }
    bool TryGetAimPoint(out WorldPosition position);
    void SpawnPing(int ownerId, WorldPosition position);
    void TickPings();
    void DrawPings();
    void ClearPings();

    /// <summary>
    /// Moves the local climber. The game refuses unless they are walking on the ground, so a
    /// caller who just brought someone back must let that land first.
    /// </summary>
    bool TryTeleportLocalPlayer(WorldPosition position, float yawDegrees, out string refusedReason);

    /// <summary>Replaces the marks a player has left on the face. They do not expire.</summary>
    void SetTrailMarks(int playerId, IReadOnlyList<WorldPosition> positions);

    void ClearTrailMarks();

    /// <summary>True while climb trails are being drawn.</summary>
    bool AreTrailsVisible { get; }

    /// <summary>Adds a point to a climber's trail. Points too close together are dropped.</summary>
    void RecordTrailPoint(int playerId, WorldPosition position);

    void SetTrailsVisible(bool visible);
    void ForgetTrail(int playerId);
    void ClearTrails();
}
