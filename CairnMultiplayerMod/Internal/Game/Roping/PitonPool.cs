namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// The group has one rack of <see cref="SharedRack"/> pitons. The leader carries it
/// and places from it. The follower brings back the ones they took off the rock.
/// A rope-up moves those onto the leader, and never past the rack. Pitons still in
/// the rock stay there.
/// </summary>
internal static class PitonPool
{
    internal const int SharedRack = 6;
    internal const int MaxContribution = 10000;
    internal const byte Offer = 0;
    internal const byte Stored = 1;

    /// <summary>
    /// How many of the follower's pitons fit on the leader. The rack is six, or the
    /// bag's own limit when that is smaller. A full rack takes none.
    /// </summary>
    internal static int FreshPitonsForLeader(int leaderCount, int followerCount, int leaderMax)
    {
        if (leaderCount < 0) leaderCount = 0;
        if (followerCount < 0) followerCount = 0;
        if (followerCount > MaxContribution) followerCount = MaxContribution;
        var cap = leaderMax <= 0 || leaderMax > SharedRack ? SharedRack : leaderMax;
        var room = cap - leaderCount;
        if (room < 0) room = 0;
        return followerCount < room ? followerCount : room;
    }

    /// <summary>Pitons the follower still holds because the rack had no room.</summary>
    internal static int KeptByFollower(int carried, int stored)
    {
        if (carried < 0) carried = 0;
        if (stored < 0) stored = 0;
        if (stored > carried) stored = carried;
        return carried - stored;
    }

    internal static byte[] WriteContribution(int linkId, int count) => Write(linkId, count, Offer);

    internal static byte[] WriteStored(int linkId, int count) => Write(linkId, count, Stored);

    internal static bool TryReadContribution(byte[] payload, out int linkId, out int count)
        => TryRead(payload, out linkId, out count, out _);

    internal static bool TryRead(byte[] payload, out int linkId, out int count, out bool stored)
    {
        linkId = 0;
        count = 0;
        stored = false;
        if (payload == null || payload.Length < 8) return false;
        linkId = ReadInt(payload, 0);
        count = ReadInt(payload, 4);
        if (linkId <= 0 || count < 0 || count > MaxContribution) return false;
        if (payload.Length < 9) return true;
        if (payload[8] == Stored)
        {
            stored = true;
            return true;
        }

        return payload[8] == Offer;
    }

    private static byte[] Write(int linkId, int count, byte kind)
    {
        var payload = new byte[9];
        WriteInt(payload, 0, linkId);
        WriteInt(payload, 4, count);
        payload[8] = kind;
        return payload;
    }

    /// <summary>
    /// A count that arrives before this climber is the leader is kept until the clip.
    /// The leader applies it on arrival.
    /// </summary>
    internal static bool ShouldHold(bool isLeader) => !isLeader;

    /// <summary>
    /// The held count belongs to this tie. A second copy, a wall untie, and a count
    /// from anyone but the follower do not move pitons.
    /// </summary>
    internal static bool ShouldApply(
        bool isLeader, int fromPlayerId, int followerId, int linkId, int tieId, int appliedTieId)
        => isLeader
           && followerId > 0
           && fromPlayerId == followerId
           && tieId > 0
           && linkId == tieId
           && appliedTieId != tieId;

    private static void WriteInt(byte[] payload, int offset, int value)
    {
        payload[offset] = (byte)value;
        payload[offset + 1] = (byte)(value >> 8);
        payload[offset + 2] = (byte)(value >> 16);
        payload[offset + 3] = (byte)(value >> 24);
    }

    private static int ReadInt(byte[] payload, int offset)
        => payload[offset]
           | (payload[offset + 1] << 8)
           | (payload[offset + 2] << 16)
           | (payload[offset + 3] << 24);
}
