using System.Collections.Generic;
using System.Numerics;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// The live rope-team chain for this client. The climber who clips is the leader.
/// Each client keys the chain by the piton id it already knows: the leader's local id,
/// or the host's id everywhere else. Untie clears it.
/// </summary>
internal static class TeamAnchorSession
{
    private static readonly TeamAnchorChain Chain = new();
    private static readonly Dictionary<uint, Vector3> Positions = new();
    private static readonly HashSet<uint> Detached = new();
    private static readonly List<uint> Order = new();
    private static readonly HashSet<uint> Live = new();
    private static readonly List<uint> Dropped = new();

    internal static bool IsLinked { get; private set; }
    internal static int LeaderId { get; private set; } = -1;
    internal static int FollowerId { get; private set; } = -1;
    internal static int LocalPlayerId { get; private set; } = -1;
    internal static int Count => Chain.Count;
    internal static bool HasProtection => Chain.Count > 0;
    internal static bool IsLocalLeader => IsLinked && LocalPlayerId == LeaderId;
    internal static bool IsLocalFollower => IsLinked && LocalPlayerId == FollowerId;
    internal static bool AllowsPlacement => TeamAnchorChain.MayPlace(IsLinked, IsLocalLeader);

    internal static void SetLocalPlayer(int playerId)
    {
        if (playerId > 0) LocalPlayerId = playerId;
    }

    internal static void OnClip(int localPlayerId, int from, int target, bool clip)
    {
        SetLocalPlayer(localPlayerId);
        if (!clip)
        {
            if (IsLinked && Pair(from, target)) Clear();
            return;
        }

        if (!RopeLinkState.IsLinked(from, target)) return;
        if (IsLinked && LeaderId == from && FollowerId == target) return;
        Clear();
        IsLinked = true;
        LeaderId = from;
        FollowerId = target;
    }

    /// <summary>The link was cleared without an unclip packet.</summary>
    internal static void ReconcileLink()
    {
        if (IsLinked && !RopeLinkState.IsLinked(LeaderId, FollowerId)) Clear();
    }

    internal static bool AllowsTake(uint pitonId)
        => TeamAnchorChain.MayTake(IsLinked, IsLocalLeader, Chain.Contains(pitonId));

    /// <summary>The leader records a piton they just placed, under their own local id.</summary>
    internal static void NoteLocalLeaderPlacement(uint id, Vector3 position)
    {
        if (!IsLocalLeader) return;
        Note(id, position);
    }

    /// <summary>Everyone except the leader records the host's id for a piton the leader placed.</summary>
    internal static void NoteRemotePlacement(int placerId, uint id, Vector3 position)
    {
        if (!IsLinked || IsLocalLeader || placerId != LeaderId) return;
        Note(id, position);
    }

    internal static void NoteRemoved(uint id)
    {
        Detached.Remove(id);
        Positions.Remove(id);
        Chain.Remove(id);
    }

    /// <summary>
    /// A host id, from <c>ServerPitonRemoved</c> or from taking someone else's piton.
    /// The leader's chain is keyed by the local id from their own announce, and the host
    /// never sends that announce back, so a host id must not splice the leader's list.
    /// </summary>
    internal static void NoteAuthoritativeRemoval(uint id)
    {
        if (IsLocalLeader) return;
        NoteRemoved(id);
    }

    internal static bool CopyPath(List<Vector3> into)
    {
        into.Clear();
        if (!HasProtection) return false;
        Chain.CopyFromOldest(Order);
        foreach (var id in Order)
            if (Positions.TryGetValue(id, out var position))
                into.Add(position);
        return into.Count > 0;
    }

    /// <summary>
    /// Pitons newer than the follower's anchor leave the follower's securing rope.
    /// One that becomes the anchor is clipped back on.
    /// </summary>
    internal static void CopyRopeEdits(List<uint> detach, List<uint> reattach)
    {
        detach.Clear();
        reattach.Clear();
        if (!IsLocalFollower) return;

        Chain.CopyFromOldest(Order);
        Live.Clear();
        foreach (var id in Order)
        {
            Live.Add(id);
            var wantDetach = Chain.IsNewerThanFollowerAnchor(id);
            var detached = Detached.Contains(id);
            if (wantDetach && !detached) detach.Add(id);
            else if (!wantDetach && detached) reattach.Add(id);
        }

        Dropped.Clear();
        foreach (var id in Detached)
            if (!Live.Contains(id)) Dropped.Add(id);
        foreach (var id in Dropped) Detached.Remove(id);
    }

    internal static void MarkDetached(uint id) => Detached.Add(id);

    internal static void MarkAttached(uint id) => Detached.Remove(id);

    internal static void Clear()
    {
        Chain.Clear();
        Positions.Clear();
        Detached.Clear();
        Order.Clear();
        Live.Clear();
        Dropped.Clear();
        IsLinked = false;
        LeaderId = -1;
        FollowerId = -1;
    }

    private static void Note(uint id, Vector3 position)
    {
        if (!Chain.Place(id)) return;
        Positions[id] = position;
    }

    private static bool Pair(int a, int b)
        => (a == LeaderId && b == FollowerId) || (a == FollowerId && b == LeaderId);
}
