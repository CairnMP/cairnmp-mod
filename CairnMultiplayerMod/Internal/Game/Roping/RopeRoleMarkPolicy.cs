namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>Which mark, if any, sits above a climber.</summary>
internal enum RopeRoleMarkKind
{
    None = 0,

    /// <summary>Three stacked stones. The climber who clipped in wears this.</summary>
    Cairn,

    /// <summary>One flat stone. The other climber wears this.</summary>
    Stone,
}

/// <summary>
/// Who wears a mark. The pair is whoever the rope team still names as leader and
/// follower: a tie, and the time after the leader leaves at a staff. A wall untie
/// and camp forget those names, so both marks leave. Hiding player names hides
/// the marks with them.
/// </summary>
internal static class RopeRoleMarkPolicy
{
    internal static bool Visible(int leaderId, int followerId, bool namesShown)
        => namesShown && leaderId > 0 && followerId > 0 && leaderId != followerId;

    internal static RopeRoleMarkKind For(int playerId, int leaderId, int followerId)
    {
        if (!Visible(leaderId, followerId, namesShown: true)) return RopeRoleMarkKind.None;
        if (playerId == leaderId) return RopeRoleMarkKind.Cairn;
        if (playerId == followerId) return RopeRoleMarkKind.Stone;
        return RopeRoleMarkKind.None;
    }
}
