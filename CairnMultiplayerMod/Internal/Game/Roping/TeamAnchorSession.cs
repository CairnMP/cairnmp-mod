using System;
using System.Collections.Generic;
using System.Numerics;
using CairnMultiplayerMod.Internal.Networking;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// The live rope-team chain for this client. The climber who clips is the leader.
/// Each client keys the chain by the piton id it already knows: the leader's local id,
/// or the host's id everywhere else. Untie clears it.
/// </summary>
internal static class TeamAnchorSession
{
    private static readonly TeamAnchorChain Chain = new();
    private static readonly TeamAnchorChain RetiredChain = new();
    private static readonly Dictionary<uint, Vector3> Positions = new();
    private static readonly Dictionary<uint, Vector3> RetiredPositions = new();
    private static readonly Dictionary<uint, bool> RetiredOwned = new();
    private static readonly HashSet<int> CatchFollowers = new();
    private static readonly HashSet<uint> Detached = new();
    private static readonly HashSet<uint> Clipped = new();
    private static readonly List<uint> Order = new();
    private static readonly HashSet<uint> Live = new();
    private static readonly List<uint> Dropped = new();
    private static readonly List<ChainRelease> Released = new();
    private static Func<bool> _inRopeTeam;
    private static bool _leftover;
    private static int _retiredFollowerId = -1;

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

    /// <summary>
    /// A tie has been made since the last camp. A wall untie leaves this set. Camp and
    /// leaving the session clear it. The climbot rope, placement, and Climbot collection
    /// follow this, not the live tie.
    /// </summary>
    internal static bool RulesArmed { get; private set; }

    /// <summary>
    /// A disconnect still marks the leader away before the pitch is cleared.
    /// A staff untie does not: that ends the partnership instead.
    /// </summary>
    internal static bool LeaderLeft { get; private set; }

    /// <summary>
    /// The recorded pair is still the one the leader left. A staff untie does not leave
    /// this set: that untie ends the partnership, and the next press starts a new one.
    /// </summary>
    internal static bool StaffHoldsPitch => IsLinked && LeaderLeft && TopAnchorSession.HasStick;

    /// <summary>The staff untie left this chain, and no new tie has archived it yet.</summary>
    internal static bool HasLeftoverCatch => _leftover && Chain.Count > 0;

    /// <summary>Pitons kept off the rope's release list when a later tie started.</summary>
    internal static bool HasRetiredCatch => RetiredChain.Count > 0;

    /// <summary>How many pitons are still catching someone after the partnership ended.</summary>
    internal static int CatchCount => RetiredChain.Count + (HasLeftoverCatch ? Chain.Count : 0);

    /// <summary>
    /// Camp, a disconnect, a death, or the menu still has rope-team gear to release.
    /// A staff untie keeps that gear, so this stays true until the gear is gone.
    /// </summary>
    internal static bool PitchRemains =>
        IsLinked || LeaderLeft || HasLeftoverCatch || HasRetiredCatch || TopAnchorSession.HasStick;

    /// <summary>
    /// This climber is the one the staff untie left on the chain. Their harness clip stays,
    /// so the pitons still catch them.
    /// </summary>
    internal static bool LocalKeepsCatch =>
        LocalPlayerId > 0
        && CatchFollowers.Contains(LocalPlayerId)
        && (HasLeftoverCatch || HasRetiredCatch);

    /// <summary>Whose harness the leftover line starts from.</summary>
    internal static int LineFollowerId => HasLeftoverCatch ? FollowerId : _retiredFollowerId;

    /// <summary>
    /// This clip is the same two climbers coming back to a chain that is already recorded.
    /// The leader rejoining is one. The follower clipping after the staff is gone is the other.
    /// </summary>
    internal static bool ResumesHeldPair(int from, int target)
        => IsLinked
           && ((LeaderId == from && FollowerId == target)
               || (LeaderLeft && LeaderId == target && FollowerId == from));

    /// <summary>
    /// This client placed the chain under its own piton numbers. Stepping into the
    /// other practice body does not change that. A real follower's chain uses the
    /// host's numbers instead, so the two stay apart.
    /// </summary>
    internal static bool OwnsLocalChainIds { get; private set; }

    internal static int LeaderId { get; private set; } = -1;
    internal static int FollowerId { get; private set; } = -1;
    internal static int LocalPlayerId { get; private set; } = -1;
    internal static int Count => Chain.Count;
    internal static bool HasProtection => Chain.Count > 0;

    /// <summary>Untie pitons still waiting to leave the rope. A hanging drop waits for these.</summary>
    internal static bool HasPendingRelease => Released.Count > 0;
    /// <summary>The practice climber and the host use one rope. A real pair has one each.</summary>
    internal static bool SharesOneRope => PracticePartner.Is(LeaderId) || PracticePartner.Is(FollowerId);

    internal static bool IsLocalLeader => IsLinked && LocalPlayerId == LeaderId;
    internal static bool IsLocalFollower => IsLinked && LocalPlayerId == FollowerId;
    internal static bool InRopeTeam => _inRopeTeam?.Invoke() == true;
    internal static bool AllowsPlacement =>
        TeamAnchorChain.MayPlace(InRopeTeam, RulesArmed, IsLocalLeader && !LeaderLeft);

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
            if (!paired && !LeaderLeft)
            {
                // The host can echo the staff untie after this client already ended the pair.
                // Drop that echo's release flag so it cannot clear the catch later.
                if (!IsLinked && _leftover)
                    PartnerFallSession.TakeForceRelease();
                return;
            }
            var forceRelease = PartnerFallSession.TakeForceRelease();
            // A death or a disconnect asks the chain to go. A voluntary unclip after a
            // stick is planted only lets the leader leave.
            if (from == LeaderId && TopAnchorSession.DropRequested)
            {
                Clear();
                return;
            }

            // The leader already left at the staff. The host echoes that unclip
            // later, and a hanging reading on the echo must not take the chain down.
            if (from == LeaderId && LeaderLeft)
            {
                TopAnchorSession.ConsumeHold();
                return;
            }

            if (from == LeaderId && !forceRelease && (TopAnchorSession.HoldRequested || TopAnchorSession.HasStick))
            {
                EndPartnership();
                TopAnchorSession.ConsumeHold();
                return;
            }

            if (paired) Clear();
            return;
        }

        // The staff is still in the ledge. The follower's button must not start a new tie.
        if (StaffHoldsPitch && from == FollowerId && target == LeaderId)
        {
            RopeLinkState.Apply(from, target, false);
            return;
        }

        if (!RopeLinkState.IsLinked(from, target)) return;
        if (ResumesHeldPair(from, target))
        {
            if (LeaderLeft)
            {
                LeaderLeft = false;
                TopAnchorSession.ConsumeHold();
            }

            return;
        }

        OpenNewPair(localPlayerId, from, target);
    }

    /// <summary>
    /// The leader unclipped at a staff, or a story scene planted one. The rope between
    /// the two climbers is over. The pitons and the staff stay clipped, and the follower
    /// is not dropped. The next press starts a new pair.
    /// </summary>
    internal static void EndPartnership()
    {
        if (!IsLinked) return;
        var leader = LeaderId;
        var follower = FollowerId;
        _leftover = Chain.Count > 0;
        // An empty pitch did not leave anyone on a catch. A later one must not
        // keep clipping them because this end remembered their id.
        if (_leftover && follower > 0)
            CatchFollowers.Add(follower);
        else if (!HasRetiredCatch)
            CatchFollowers.Clear();
        IsLinked = false;
        LeaderLeft = false;
        TopAnchorSession.ConsumeHold();
        if (leader > 0 && follower > 0)
            RopeLinkState.Apply(leader, follower, false);
    }

    /// <summary>A leader unclip while a staff is planted. The follower stays on the chain.</summary>
    internal static bool StaffUntieKeepsFollower(bool hasStick, int from)
        => hasStick && IsLinked && from == LeaderId;

    /// <summary>
    /// The practice climber and the local climber trade jobs. The piton numbers stay
    /// with whoever placed them, and a staff holding the pitch stays where it is.
    /// A real pair is left alone.
    /// </summary>
    internal static bool ExchangePracticeRoles(int practiceId)
    {
        if (!IsLinked || practiceId <= 0) return false;
        if (LeaderId != practiceId && FollowerId != practiceId) return false;
        if (LocalPlayerId != LeaderId && LocalPlayerId != FollowerId) return false;
        var leader = LeaderId;
        LeaderId = FollowerId;
        FollowerId = leader;
        return true;
    }

    /// <summary>Camp, or the session ending. A wall untie does not call this.</summary>
    internal static void DisarmRules() => RulesArmed = false;

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

    /// <summary>
    /// A hold that is still linked can come back when its story stick is gone.
    /// A staff untie has already ended the pair, so taking the stick out does not rejoin it.
    /// </summary>
    internal static void RejoinIfStillTied()
    {
        if (!IsLinked || !LeaderLeft || TopAnchorSession.HasCutsceneStick) return;
        if (!RopeLinkState.IsLinked(LeaderId, FollowerId)) return;
        LeaderLeft = false;
        TopAnchorSession.ConsumeHold();
    }

    /// <summary>
    /// A piton may be picked up. The leader's chain is keyed by local announce ids and
    /// everyone else's by host ids, so a shared number is on the chain only in that namespace.
    /// A staff id is never gear.
    /// </summary>
    internal static bool AllowsTake(uint pitonId, bool ownLocalId)
    {
        if (TopAnchorPolicy.IsStickId(pitonId)) return false;
        if (OnCatch(pitonId, ownLocalId))
            return CatchFollowers.Contains(LocalPlayerId);
        return TeamAnchorChain.MayTake(IsLinked, IsLocalLeader, IsChainKey(pitonId, ownLocalId));
    }

    /// <summary>True when this id is still the catch left by a staff untie.</summary>
    internal static bool Protects(uint pitonId)
        => (HasLeftoverCatch && Chain.Contains(pitonId)) || RetiredChain.Contains(pitonId);

    /// <summary>
    /// True when <paramref name="pitonId"/> is a key of this client's chain.
    /// The client who placed them keeps those numbers, including after a practice
    /// switch. Everyone else uses the host's numbers. Both clients share a staff id.
    /// </summary>
    internal static bool IsChainKey(uint pitonId, bool ownLocalId)
    {
        if (!Chain.Contains(pitonId)) return false;
        if (TopAnchorPolicy.IsStickId(pitonId)) return true;
        return OwnsLocalChainIds == ownLocalId;
    }

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
        if (anchor == 0 || (!IsLinked && !_leftover) || !Positions.TryGetValue(anchor, out position)) return false;
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
        if (IsLocalLeader || placerId != LeaderId) return;
        if (!IsLinked && !_leftover) return;
        Note(id, position);
    }

    internal static void NoteRemoved(uint id)
    {
        RemoveLive(id);
        RemoveRetired(id);
    }

    /// <summary>
    /// One of our pitons left the lifeline. The id is the local announce id, and only the
    /// client who placed the chain stores it under those ids. On a real follower the same
    /// number can be a host id already on the chain, so it must not be spliced out.
    /// </summary>
    internal static void NoteLocalRemoval(uint localId)
    {
        if (OwnsLocalChainIds) RemoveLive(localId);
        // A retired id keeps the namespace it was archived under. The live chain's
        // current namespace must not splice a catch that used the other numbers.
        if (RetiredOwned.TryGetValue(localId, out var owned) && owned)
            RemoveRetired(localId);
    }

    /// <summary>
    /// A host id, from <c>ServerPitonRemoved</c> or from taking someone else's piton.
    /// The placing client's chain is keyed by the local id from their own announce, and
    /// the host never sends that announce back, so a host id must not splice that list.
    /// That stays true after they step into the follower's body.
    /// </summary>
    internal static void NoteAuthoritativeRemoval(uint id)
    {
        if (!OwnsLocalChainIds) RemoveLive(id);
        if (RetiredOwned.TryGetValue(id, out var owned) && !owned)
            RemoveRetired(id);
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

    /// <summary>The catch archived when a new tie started. Oldest piton first.</summary>
    internal static bool CopyRetiredPath(List<Vector3> into)
    {
        into.Clear();
        if (!HasRetiredCatch) return false;
        RetiredChain.CopyFromOldest(Order);
        foreach (var id in Order)
            if (RetiredPositions.TryGetValue(id, out var position))
                into.Add(position);
        return into.Count > 0;
    }

    internal static void CopyRetiredIds(List<uint> into)
    {
        into.Clear();
        if (!HasRetiredCatch) return;
        RetiredChain.CopyFromOldest(Order);
        foreach (var id in Order)
            if (RetiredPositions.ContainsKey(id))
                into.Add(id);
    }

    /// <summary>
    /// Pitons newer than the follower's anchor leave the follower's securing rope.
    /// The anchor is queued until it has been clipped on. <c>AddPiton</c> does not
    /// clip a quickdraw while that rope is off the harness, which is how the
    /// follower's first piton arrives. One that becomes the anchor is clipped again.
    /// </summary>
    internal static void CopyRopeEdits(List<uint> detach, List<uint> reattach)
    {
        detach.Clear();
        reattach.Clear();
        if (!IsLocalFollower || !TeamAnchorChain.FollowerUnclipsNewerPitons(SharesOneRope)) return;

        Chain.CopyFromOldest(Order);
        Live.Clear();
        foreach (var id in Order)
        {
            Live.Add(id);
            var wantDetach = Chain.IsNewerThanFollowerAnchor(id);
            var detached = Detached.Contains(id);
            if (wantDetach && !detached) detach.Add(id);
            else if (!wantDetach && !Clipped.Contains(id)) reattach.Add(id);
        }

        Dropped.Clear();
        foreach (var id in Detached)
            if (!Live.Contains(id)) Dropped.Add(id);
        foreach (var id in Dropped) Detached.Remove(id);
        Dropped.Clear();
        foreach (var id in Clipped)
            if (!Live.Contains(id)) Dropped.Add(id);
        foreach (var id in Dropped) Clipped.Remove(id);
    }

    internal static void MarkDetached(uint id)
    {
        Detached.Add(id);
        Clipped.Remove(id);
    }

    internal static void MarkAttached(uint id)
    {
        Detached.Remove(id);
        Clipped.Add(id);
    }

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
        WipeLiveChain();
        RetiredChain.Clear();
        RetiredPositions.Clear();
        RetiredOwned.Clear();
        CatchFollowers.Clear();
        _retiredFollowerId = -1;
        _leftover = false;
        Order.Clear();
        Live.Clear();
        Dropped.Clear();
        IsLinked = false;
        LeaderLeft = false;
        LeaderId = -1;
        FollowerId = -1;
        OwnsLocalChainIds = false;
        TopAnchorSession.Reset();
    }

    /// <summary>
    /// A new rope-up. A catch left by a staff untie stays clipped and is not this pair's
    /// chain. Any other live chain comes off the rope.
    /// </summary>
    private static void OpenNewPair(int localPlayerId, int from, int target)
    {
        if (_leftover && Chain.Count > 0)
            ArchiveLeftover();
        else
        {
            QueueLive(Chain, OwnsLocalChainIds);
            WipeLiveChain();
            if (RetiredChain.Count == 0)
                TopAnchorSession.Reset();
        }

        IsLinked = true;
        RulesArmed = true;
        LeaderLeft = false;
        LeaderId = from;
        FollowerId = target;
        OwnsLocalChainIds = localPlayerId == from;
        _leftover = false;
    }

    private static void ArchiveLeftover()
    {
        Chain.CopyFromOldest(Order);
        foreach (var id in Order)
        {
            // The same number can already be catching from an earlier pitch.
            // That one keeps its namespace. This pitch's copy is not a second entry.
            if (!RetiredChain.Place(id)) continue;
            RetiredOwned[id] = OwnsLocalChainIds;
            if (Positions.TryGetValue(id, out var position))
                RetiredPositions[id] = position;
        }

        if (FollowerId > 0)
        {
            _retiredFollowerId = FollowerId;
            CatchFollowers.Add(FollowerId);
        }

        WipeLiveChain();
        _leftover = false;
    }

    private static void WipeLiveChain()
    {
        Chain.Clear();
        Positions.Clear();
        Detached.Clear();
        Clipped.Clear();
    }

    private static void RemoveLive(uint id)
    {
        Detached.Remove(id);
        Clipped.Remove(id);
        Positions.Remove(id);
        Chain.Remove(id);
        if (_leftover && Chain.Count == 0) _leftover = false;
        if (!HasLeftoverCatch && !HasRetiredCatch) CatchFollowers.Clear();
    }

    private static void RemoveRetired(uint id)
    {
        RetiredPositions.Remove(id);
        RetiredOwned.Remove(id);
        RetiredChain.Remove(id);
        if (!HasLeftoverCatch && !HasRetiredCatch) CatchFollowers.Clear();
    }

    private static bool OnCatch(uint pitonId, bool ownLocalId)
    {
        if (HasLeftoverCatch && IsChainKey(pitonId, ownLocalId)) return true;
        return RetiredChain.Contains(pitonId)
               && RetiredOwned.TryGetValue(pitonId, out var owned)
               && owned == ownLocalId;
    }

    private static void QueueRelease()
    {
        QueueLive(Chain, OwnsLocalChainIds);
        RetiredChain.CopyFromOldest(Order);
        foreach (var id in Order)
        {
            if (id == 0 || TopAnchorPolicy.IsStickId(id)) continue;
            var owned = RetiredOwned.TryGetValue(id, out var local) && local;
            var release = new ChainRelease(id, owned);
            if (!Released.Contains(release)) Released.Add(release);
        }
    }

    private static void QueueLive(TeamAnchorChain chain, bool localIds)
    {
        chain.CopyFromOldest(Order);
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
