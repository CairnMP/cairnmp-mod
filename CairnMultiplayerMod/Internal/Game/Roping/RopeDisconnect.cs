using System;
using System.Collections.Generic;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// A climber has gone, or this session is ending. The tie ends completely: a planted
/// staff does not keep the pitch. The callback runs while the tie is still up, so a
/// hanging climber can be dropped before the rope is taken down.
/// </summary>
internal static class RopeDisconnect
{
    internal static bool TiedTo(int localId, int otherId)
    {
        if (otherId <= 0 || localId <= 0 || localId == otherId) return false;
        if (RopeLinkState.IsLinked(localId, otherId)) return true;
        if (!TeamAnchorSession.IsLinked) return false;
        var leader = TeamAnchorSession.LeaderId;
        var follower = TeamAnchorSession.FollowerId;
        return (localId == leader || localId == follower)
               && (otherId == leader || otherId == follower);
    }

    /// <summary>
    /// End the tie with this one climber. Returns false when they were not the partner.
    /// A hang arms the drop before the link is removed.
    /// </summary>
    internal static bool Untie(int localId, int otherId, bool localHanging, Action whileTied)
    {
        if (!TiedTo(localId, otherId)) return false;
        if (localHanging)
            PartnerFallSession.ArmDrop();
        whileTied?.Invoke();
        // A disconnect is not a leader stepping off at the anchor. The pitch goes.
        TopAnchorSession.RequestDrop();
        PartnerFallSession.ForceRelease = true;
        RopeLinkState.Apply(otherId, localId, false);
        TeamAnchorSession.OnClip(localId, otherId, localId, false);
        return true;
    }

    /// <summary>
    /// Untie every partner, then release the rope. The release is last so a hang
    /// still has a rope to leave.
    /// </summary>
    internal static void EndSession(
        int localId,
        IReadOnlyList<int> partners,
        bool localHanging,
        Action<int> whileTied,
        Action releaseRope)
    {
        if (partners != null)
        {
            for (var i = 0; i < partners.Count; i++)
            {
                var partner = partners[i];
                Untie(localId, partner, localHanging, () => whileTied?.Invoke(partner));
            }
        }

        releaseRope?.Invoke();
    }
}
