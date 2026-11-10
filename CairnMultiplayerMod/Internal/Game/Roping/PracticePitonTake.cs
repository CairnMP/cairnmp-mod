using CairnMultiplayerMod.Internal.Networking;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// The practice follower takes a chain piton from his own position.
/// The leader's body is not an input: a chain piton stays out of the leader's reach.
/// </summary>
internal static class PracticePitonTake
{
    internal static bool OffersChainPiton(
        bool linked, bool localIsLeader, int followerId, bool onChain, float distanceMeters)
    {
        if (!linked || !localIsLeader || !onChain || !PracticePartner.Is(followerId)) return false;
        if (!float.IsFinite(distanceMeters) || distanceMeters < 0f) return false;
        return distanceMeters <= PitonRecoveryPolicy.ReachMeters;
    }
}
