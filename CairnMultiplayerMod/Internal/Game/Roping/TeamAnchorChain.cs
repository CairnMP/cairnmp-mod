using System.Collections.Generic;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// The pitons on one rope team, oldest at the follower and newest at the leader.
/// A climber is never an anchor. An empty chain protects nobody.
/// </summary>
internal sealed class TeamAnchorChain
{
    private sealed class Node
    {
        internal uint Id;
        internal Node Parent;
        internal Node Child;
    }

    private readonly Dictionary<uint, Node> _nodes = new();
    private Node _oldest;

    internal int Count => _nodes.Count;
    internal uint LeaderAnchor { get; private set; }
    internal uint FollowerAnchor { get; private set; }

    /// <summary>
    /// Outside Rope Team, Cairn decides. Inside it, Cairn still decides until a tie has been
    /// made since camp. After that, only the leader who is still on the rope places. A wall
    /// untie does not give placement back.
    /// </summary>
    internal static bool MayPlace(bool ropeTeam, bool rulesArmed, bool leaderStillOnRope)
        => !ropeTeam || !rulesArmed || leaderStillOnRope;

    /// <summary>The leader cannot take a piton that is on the chain.</summary>
    internal static bool MayTake(bool linked, bool isLeader, bool onChain)
        => !(linked && isLeader && onChain);

    /// <summary>
    /// Cairn's paid-out stop follows the climbot coil on this harness. It stays only while
    /// they are the leader and still tied. After they unclip at a staff, they remain the
    /// leader on the held chain, and the coil has to come off or the ledge keeps the stop.
    /// </summary>
    internal static bool KeepsClimbotCoil(bool isLocalLeader, bool leaderLeft)
        => isLocalLeader && !leaderLeft;

    /// <summary>
    /// The follower is caught by the piton at their anchor. That clip stays on the harness.
    /// The climbot coil stays off, so Climbot is not the belay. An empty chain protects nobody.
    /// </summary>
    internal static bool FollowerKeepsSecuringClip(bool isLocalFollower, bool hasProtection)
        => isLocalFollower && hasProtection;

    /// <summary>
    /// The same piton offered as a rest. Rest stays when the rope already runs through
    /// it and the harness is clipped on. A clip onto chain gear that is missing either
    /// of those is refused. The caller has already checked that the piton is on the chain.
    /// </summary>
    internal static bool BlocksFreshClip(bool keepsCatch, bool ropeRunsThroughIt, bool harnessClipped)
        => keepsCatch && !(ropeRunsThroughIt && harnessClipped);

    /// <summary>
    /// A real follower has their own copy of the rope, so pitons above their anchor leave
    /// that copy. The practice pair shares one rope. Unclipping those pitons takes the
    /// quickdraw off the only rope, and then they cannot be taken.
    /// </summary>
    internal static bool FollowerUnclipsNewerPitons(bool sharedRope)
        => !sharedRope;

    internal bool Contains(uint id) => id != 0 && _nodes.ContainsKey(id);

    internal uint ParentOf(uint id)
        => _nodes.TryGetValue(id, out var node) && node.Parent != null ? node.Parent.Id : 0;

    /// <summary>Append a piton placed by the leader. A repeated id changes nothing.</summary>
    internal bool Place(uint id)
    {
        if (id == 0 || _nodes.ContainsKey(id)) return false;
        var node = new Node { Id = id };
        _nodes[id] = node;
        if (_oldest == null)
        {
            _oldest = node;
            LeaderAnchor = FollowerAnchor = id;
            return true;
        }

        var leaf = _oldest;
        while (leaf.Child != null) leaf = leaf.Child;
        node.Parent = leaf;
        leaf.Child = node;
        LeaderAnchor = id;
        return true;
    }

    /// <summary>
    /// Splice a piton out. The follower moves to its child. The leader moves to its child,
    /// or to its parent when it was the newest piton.
    /// </summary>
    internal bool Remove(uint id)
    {
        if (!_nodes.TryGetValue(id, out var node)) return false;
        var parent = node.Parent;
        var child = node.Child;
        if (parent != null) parent.Child = child;
        else _oldest = child;
        if (child != null) child.Parent = parent;

        if (FollowerAnchor == id) FollowerAnchor = child?.Id ?? 0;
        if (LeaderAnchor == id) LeaderAnchor = child?.Id ?? parent?.Id ?? 0;

        _nodes.Remove(id);
        if (_nodes.Count == 0)
        {
            _oldest = null;
            LeaderAnchor = FollowerAnchor = 0;
        }
        return true;
    }

    /// <summary>True when the piton sits further toward the leader than the follower's anchor.</summary>
    internal bool IsNewerThanFollowerAnchor(uint id)
    {
        if (id == 0 || FollowerAnchor == 0 || id == FollowerAnchor) return false;
        if (!_nodes.TryGetValue(FollowerAnchor, out var node)) return false;
        for (node = node.Child; node != null; node = node.Child)
            if (node.Id == id) return true;
        return false;
    }

    internal void CopyFromOldest(List<uint> into)
    {
        into.Clear();
        for (var node = _oldest; node != null; node = node.Child)
            into.Add(node.Id);
    }

    internal void Clear()
    {
        _nodes.Clear();
        _oldest = null;
        LeaderAnchor = FollowerAnchor = 0;
    }
}
