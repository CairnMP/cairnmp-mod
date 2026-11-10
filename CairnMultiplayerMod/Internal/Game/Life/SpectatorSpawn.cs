namespace CairnMultiplayerMod.Internal.Game.Life;

/// <summary>A camera pose the spectator seat can open from.</summary>
internal readonly struct SpectatorViewpoint
{
    internal readonly bool HasPose;
    internal readonly float X;
    internal readonly float Y;
    internal readonly float Z;
    internal readonly float Pitch;
    internal readonly float Yaw;

    private SpectatorViewpoint(bool hasPose, float x, float y, float z, float pitch, float yaw)
    {
        HasPose = hasPose;
        X = x;
        Y = y;
        Z = z;
        Pitch = pitch;
        Yaw = yaw;
    }

    /// <summary>A pose with a real position. NaN and infinity are not a place to put the camera.</summary>
    internal static SpectatorViewpoint At(float x, float y, float z, float pitch, float yaw)
    {
        if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z) || !IsFinite(pitch) || !IsFinite(yaw))
            return default;
        return new SpectatorViewpoint(true, x, y, z, pitch, yaw);
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}

/// <summary>
/// Which pose the spectator camera opens on.
///
/// The remembered pose is the view from while the climber was alive. The live camera is only
/// a fallback: by the time death opens the seat, Cairn's hover shot may already be hundreds
/// of metres below the face.
/// </summary>
internal static class SpectatorSpawn
{
    internal static SpectatorViewpoint Choose(SpectatorViewpoint remembered, SpectatorViewpoint live)
    {
        if (remembered.HasPose) return remembered;
        if (live.HasPose) return live;
        return default;
    }
}
