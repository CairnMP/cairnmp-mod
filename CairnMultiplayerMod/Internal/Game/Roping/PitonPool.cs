namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// On a new rope link the two carried piton counts become one pile on the leader.
/// Anything past the leader's bag limit is dropped. The follower's bag ends empty.
/// </summary>
internal static class PitonPool
{
    internal const int MaxContribution = 10000;

    /// <summary>
    /// How many new pitons the leader gains. A non-positive <paramref name="leaderMax"/>
    /// means the bag did not report a limit, so the caller asks for the whole contribution
    /// and stops when the game refuses another piton.
    /// </summary>
    internal static int FreshPitonsForLeader(int leaderCount, int followerCount, int leaderMax)
    {
        if (leaderCount < 0) leaderCount = 0;
        if (followerCount < 0) followerCount = 0;
        if (followerCount > MaxContribution) followerCount = MaxContribution;
        if (leaderMax <= 0) return followerCount;
        var room = leaderMax - leaderCount;
        if (room < 0) room = 0;
        return followerCount < room ? followerCount : room;
    }

    internal static byte[] WriteContribution(int linkId, int count)
    {
        var payload = new byte[8];
        WriteInt(payload, 0, linkId);
        WriteInt(payload, 4, count);
        return payload;
    }

    internal static bool TryReadContribution(byte[] payload, out int linkId, out int count)
    {
        linkId = 0;
        count = 0;
        if (payload == null || payload.Length < 8) return false;
        linkId = ReadInt(payload, 0);
        count = ReadInt(payload, 4);
        return linkId > 0 && count >= 0 && count <= MaxContribution;
    }

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
