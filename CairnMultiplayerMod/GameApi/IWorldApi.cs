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
}
