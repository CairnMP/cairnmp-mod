#nullable enable

namespace CairnMultiplayerMod.Internal.Networking.Authoritative;

/// <summary>What the host last heard about a guest's native rope. Missing means not yet reported.</summary>
internal enum NativePitonReport : byte
{
    Unknown = 0,
    Clear = 1,
    Holding = 2,
}

/// <summary>
/// A tie is broadcast only when both climbers are known to be clear.
/// Unknown is not clear: a missing report must not open the rope.
/// </summary>
internal readonly struct TieDecision
{
    internal TieDecision(bool allowed, int mustRecallPlayerId, bool pitonOut)
    {
        Allowed = allowed;
        MustRecallPlayerId = mustRecallPlayerId;
        PitonOut = pitonOut;
    }

    internal bool Allowed { get; }
    internal int MustRecallPlayerId { get; }
    internal bool PitonOut { get; }
}

internal static class RopeTieAdmission
{
    internal static TieDecision Decide(
        int presserId, int partnerId, NativePitonReport presser, NativePitonReport partner)
    {
        if (presser != NativePitonReport.Clear)
            return new TieDecision(false, presserId, presser == NativePitonReport.Holding);
        if (partner != NativePitonReport.Clear)
            return new TieDecision(false, partnerId, partner == NativePitonReport.Holding);
        return new TieDecision(true, 0, false);
    }
}

/// <summary>Host-side memory of guest reports. Dropped when that player leaves.</summary>
internal sealed class NativePitonReports
{
    private readonly System.Collections.Generic.Dictionary<int, bool> _holding = new();

    internal void Set(int playerId, bool holding) => _holding[playerId] = holding;

    internal void Forget(int playerId) => _holding.Remove(playerId);

    internal void Clear() => _holding.Clear();

    internal NativePitonReport Report(int playerId, bool isLocal, bool localHolding)
    {
        if (isLocal) return localHolding ? NativePitonReport.Holding : NativePitonReport.Clear;
        if (!_holding.TryGetValue(playerId, out var holding)) return NativePitonReport.Unknown;
        return holding ? NativePitonReport.Holding : NativePitonReport.Clear;
    }
}
