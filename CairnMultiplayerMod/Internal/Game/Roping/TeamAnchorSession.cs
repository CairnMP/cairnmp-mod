using System;
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
    private static readonly List<ChainRelease> Released = new();
    private static Func<bool> _inRopeTeam;

    internal readonly struct ChainRelease
    {
        internal ChainRelease(uint id, bool localId)
        {
            Id = id;
            LocalId = localId;
        }

        /// <summary>Leader announce id when <see cref="LocalId"/> is set, otherwise the host id.</summary>
        internal uint Id { get; }

        internal bool LocalId { get; }
    }

    internal static bool IsLinked { get; private set; }

    /// <summary>The leader unclipped after a stick was planted. The chain stays for the follower.</summary>
    internal static bool LeaderLeft { get; private set; }

    internal static int LeaderId { get; private set; } = -1;
    internal static int FollowerId { get; private set; } = -1;
    internal static int LocalPlayerId { get; private set; } = -1;
    internal static int Count => Chain.Count;
    internal static bool HasProtection => Chain.Count > 0;
    internal static bool IsLocalLeader => IsLinked && LocalPlayerId == LeaderId;
    internal static bool IsLocalFollower => IsLinked && LocalPlayerId == FollowerId;
    internal static bool InRopeTeam => _inRopeTeam?.Invoke() == true;
    internal static bool AllowsPlacement => TeamAnchorChain.MayPlace(InRopeTeam, IsLinked && !LeaderLeft, IsLocalLeader);
    internal static bool LeaderIsUnprotected => IsLinked && Chain.LeaderAnchor == 0;

    internal static void Bind(Func<bool> inRopeTeam)
        => _inRopeTeam = inRopeTeam ?? throw new ArgumentNullException(nameof(inRopeTeam));

    internal static void SetLocalPlayer(int playerId)
    {
        if (playerId > 0) LocalPlayerId = playerId;
    }

    internal static void OnClip(int localPlayerId, int from, int target, bool clip)
    {
        SetLocalPlayer(localPlayerId);
        if (!clip)
        {
            var paired = IsLinked && Pair(from, target);
            if (!paired && !LeaderLeft) return;
            // A death or a disconnect asks the chain to go. A voluntary unclip after a
            // stick is planted only lets the leader leave.
            if (from == LeaderId && TopAnchorSession.DropRequested)
            {
                Clear();
                return;
            }

            if (from == LeaderId && (TopAnchorSession.HoldRequested || TopAnchorSession.HasStick))
            {
                HoldForFollower();
                TopAnchorSession.ConsumeHold();
                return;
            }

            if (paired) Clear();
            return;
        }

        if (!RopeLinkState.IsLinked(from, target)) return;
        if (IsLinked && LeaderId == from && FollowerId == target)
        {
            if (LeaderLeft)
            {
                LeaderLeft = false;
                TopAnchorSession.ConsumeHold();
            }

            return;
        }

        Clear();
        IsLinked = true;
        LeaderId = from;
        FollowerId = target;
    }

    /// <summary>The link was cleared without an unclip packet.</summary>
    internal static void ReconcileLink()
    {
        if (LeaderLeft) return;
        if (IsLinked && !RopeLinkState.IsLinked(LeaderId, FollowerId)) Clear();
    }

    /// <summary>
    /// The partnership is over. Every piton stays on the chain. The stick remains the top end.
    /// It becomes the follower's anchor only after the pitons below it are gone.
    /// </summary>
    internal static void HoldForFollower()
    {
        if (!IsLinked) return;
        LeaderLeft = true;
    }

    internal static bool AllowsTake(uint pitonId)
        => !TopAnchorPolicy.IsStickId(pitonId)
           && TeamAnchorChain.MayTake(IsLinked, IsLocalLeader, Chain.Contains(pitonId));

    /// <summary>The piton the leader is on, and where it was placed.</summary>
    internal static bool TryGetLeaderAnchor(out uint id, out Vector3 position)
        => TryGetAnchor(Chain.LeaderAnchor, out id, out position);

    /// <summary>The piton the follower is on, and where it was placed.</summary>
    internal static bool TryGetFollowerAnchor(out uint id, out Vector3 position)
        => TryGetAnchor(Chain.FollowerAnchor, out id, out position);

    private static bool TryGetAnchor(uint anchor, out uint id, out Vector3 position)
    {
        id = 0;
        position = default;
        if (!IsLinked || anchor == 0 || !Positions.TryGetValue(anchor, out position)) return false;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
        {
            position = default;
            return false;
        }

        id = anchor;
        return true;
    }

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

    /// <summary>Same order as <see cref="CopyPath"/>: oldest piton first, and only ids that have a position.</summary>
    internal static void CopyIds(List<uint> into)
    {
        into.Clear();
        if (!HasProtection) return;
        Chain.CopyFromOldest(Order);
        foreach (var id in Order)
            if (Positions.ContainsKey(id))
                into.Add(id);
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

    /// <summary>Pitons the last untie took off the rope. They stay queued until each one is unclipped.</summary>
    internal static void CopyReleased(List<ChainRelease> into)
    {
        into.Clear();
        into.AddRange(Released);
    }

    internal static void MarkReleased(ChainRelease piton)
    {
        for (var i = 0; i < Released.Count; i++)
        {
            if (Released[i].Id != piton.Id || Released[i].LocalId != piton.LocalId) continue;
            Released.RemoveAt(i);
            return;
        }
    }

    internal static void DiscardRelease() => Released.Clear();

    internal static void Clear()
    {
        QueueRelease();
        Chain.Clear();
        Positions.Clear();
        Detached.Clear();
        Order.Clear();
        Live.Clear();
        Dropped.Clear();
        IsLinked = false;
        LeaderLeft = false;
        LeaderId = -1;
        FollowerId = -1;
        TopAnchorSession.Reset();
    }

    private static void QueueRelease()
    {
        var localIds = IsLocalLeader;
        Chain.CopyFromOldest(Order);
        foreach (var id in Order)
        {
            if (id == 0 || TopAnchorPolicy.IsStickId(id)) continue;
            var release = new ChainRelease(id, localIds);
            if (!Released.Contains(release)) Released.Add(release);
        }
    }

    private static void Note(uint id, Vector3 position)
    {
        if (!Chain.Place(id)) return;
        Positions[id] = position;
    }

    private static bool Pair(int a, int b)
        => (a == LeaderId && b == FollowerId) || (a == FollowerId && b == LeaderId);
}
