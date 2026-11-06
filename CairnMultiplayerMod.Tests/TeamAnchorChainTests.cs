using System.Collections.Generic;
using System.Numerics;
using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class TeamAnchorChainTests
{
    [Fact]
    public void TheFirstPitonAnchorsBothClimbers()
    {
        var chain = new TeamAnchorChain();

        Assert.True(chain.Place(1));

        Assert.Equal(1u, chain.LeaderAnchor);
        Assert.Equal(1u, chain.FollowerAnchor);
        Assert.Equal(0u, chain.ParentOf(1));
    }

    [Fact]
    public void ALaterPitonAnchorsOnlyTheLeaderAndParentsToThePreviousOne()
    {
        var chain = new TeamAnchorChain();
        chain.Place(1);
        chain.Place(2);
        chain.Place(3);

        Assert.Equal(3u, chain.LeaderAnchor);
        Assert.Equal(1u, chain.FollowerAnchor);
        Assert.Equal(2u, chain.ParentOf(3));
        Assert.Equal(1u, chain.ParentOf(2));
    }

    [Fact]
    public void PlacingTheSamePitonAgainChangesNothing()
    {
        var chain = new TeamAnchorChain();
        chain.Place(1);
        chain.Place(2);

        Assert.False(chain.Place(2));
        Assert.Equal(2, chain.Count);
        Assert.Equal(2u, chain.LeaderAnchor);
    }

    [Fact]
    public void RemovingTheFollowersAnchorMovesTheFollowerToItsChild()
    {
        var chain = new TeamAnchorChain();
        chain.Place(1);
        chain.Place(2);
        chain.Place(3);

        Assert.True(chain.Remove(1));

        Assert.Equal(2u, chain.FollowerAnchor);
        Assert.Equal(3u, chain.LeaderAnchor);
        Assert.Equal(0u, chain.ParentOf(2));
    }

    [Fact]
    public void RemovingAMiddlePitonOnlySplicesTheChain()
    {
        var chain = new TeamAnchorChain();
        chain.Place(1);
        chain.Place(2);
        chain.Place(3);

        Assert.True(chain.Remove(2));

        Assert.Equal(1u, chain.FollowerAnchor);
        Assert.Equal(3u, chain.LeaderAnchor);
        Assert.Equal(1u, chain.ParentOf(3));
        Assert.False(chain.Contains(2));
    }

    [Fact]
    public void RemovingTheLeadersNewestPitonMovesTheLeaderToItsParent()
    {
        var chain = new TeamAnchorChain();
        chain.Place(1);
        chain.Place(2);
        chain.Place(3);

        Assert.True(chain.Remove(3));

        Assert.Equal(1u, chain.FollowerAnchor);
        Assert.Equal(2u, chain.LeaderAnchor);
        Assert.Equal(1u, chain.ParentOf(2));
        Assert.True(chain.IsNewerThanFollowerAnchor(2));
    }

    [Fact]
    public void RemovingTheLastPitonClearsBothAnchors()
    {
        var chain = new TeamAnchorChain();
        chain.Place(1);
        chain.Place(2);
        chain.Remove(1);

        Assert.Equal(2u, chain.LeaderAnchor);
        Assert.Equal(2u, chain.FollowerAnchor);
        Assert.True(chain.Remove(2));
        Assert.Equal(0, chain.Count);
        Assert.Equal(0u, chain.LeaderAnchor);
        Assert.Equal(0u, chain.FollowerAnchor);
    }

    [Fact]
    public void AMissingPitonIsLeftAlone()
    {
        var chain = new TeamAnchorChain();
        chain.Place(1);

        Assert.False(chain.Remove(9));
        Assert.Equal(1u, chain.LeaderAnchor);
    }

    [Fact]
    public void TheFollowerCannotPlaceAndTheLeaderCannotTakeAChainPiton()
    {
        Assert.False(TeamAnchorChain.MayPlace(ropeTeam: true, linked: false, isLeader: false));
        Assert.False(TeamAnchorChain.MayPlace(ropeTeam: true, linked: false, isLeader: true));
        Assert.False(TeamAnchorChain.MayPlace(ropeTeam: true, linked: true, isLeader: false));
        Assert.True(TeamAnchorChain.MayPlace(ropeTeam: true, linked: true, isLeader: true));
        Assert.True(TeamAnchorChain.MayPlace(ropeTeam: false, linked: false, isLeader: false));

        Assert.False(TeamAnchorChain.MayTake(linked: true, isLeader: true, onChain: true));
        Assert.True(TeamAnchorChain.MayTake(linked: true, isLeader: true, onChain: false));
        Assert.True(TeamAnchorChain.MayTake(linked: true, isLeader: false, onChain: true));
    }

    [Fact]
    public void TheClimberWhoClipsLeadsUntilTheRopeIsUntied()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);

        Assert.True(TeamAnchorSession.IsLocalLeader);
        Assert.Equal(4, TeamAnchorSession.LeaderId);
        TeamAnchorSession.NoteLocalLeaderPlacement(3, new Vector3(1f, 2f, 3f));
        Assert.True(TeamAnchorSession.HasProtection);
        Assert.Equal(1, TeamAnchorSession.Count);

        RopeLinkState.Apply(4, 7, false);
        TeamAnchorSession.OnClip(4, 4, 7, false);
        Assert.False(TeamAnchorSession.IsLinked);
        Assert.False(TeamAnchorSession.HasProtection);
    }

    [Fact]
    public void TheFollowerRecordsTheLeadersPitonsAndIgnoresTheirOwn()
    {
        Reset();
        TeamAnchorSession.Bind(() => true);
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(7, 4, 7, true);

        TeamAnchorSession.NoteRemotePlacement(4, 9, new Vector3(0f, 1f, 0f));
        TeamAnchorSession.NoteRemotePlacement(7, 10, new Vector3(0f, 2f, 0f));

        Assert.Equal(1, TeamAnchorSession.Count);
        Assert.True(TeamAnchorSession.IsLocalFollower);
        Assert.False(TeamAnchorSession.AllowsPlacement);
    }

    [Fact]
    public void TheLeaderIgnoresTheHostIdForAPitonTheyAlreadyPlaced()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);

        TeamAnchorSession.NoteRemotePlacement(4, 99, new Vector3(1f, 0f, 0f));
        TeamAnchorSession.NoteLocalLeaderPlacement(1, new Vector3(1f, 0f, 0f));

        Assert.Equal(1, TeamAnchorSession.Count);
    }

    [Fact]
    public void TheFollowerUnclipsPitonsNewerThanTheirAnchorAndClipsOneBack()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(7, 4, 7, true);
        TeamAnchorSession.NoteRemotePlacement(4, 1, new Vector3(0f, 1f, 0f));
        TeamAnchorSession.NoteRemotePlacement(4, 2, new Vector3(0f, 2f, 0f));
        TeamAnchorSession.NoteRemotePlacement(4, 3, new Vector3(0f, 3f, 0f));

        var detach = new List<uint>();
        var reattach = new List<uint>();
        TeamAnchorSession.CopyRopeEdits(detach, reattach);

        Assert.Equal(new uint[] { 2, 3 }, detach);
        Assert.Empty(reattach);

        TeamAnchorSession.MarkDetached(2);
        TeamAnchorSession.MarkDetached(3);
        TeamAnchorSession.NoteRemoved(1);
        TeamAnchorSession.CopyRopeEdits(detach, reattach);

        Assert.Empty(detach);
        Assert.Equal(new uint[] { 2 }, reattach);
    }

    [Fact]
    public void TheLeaderIgnoresAHostIdThatCollidesWithALocalPiton()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        TeamAnchorSession.NoteLocalLeaderPlacement(1, new Vector3(0f, 1f, 0f));
        TeamAnchorSession.NoteLocalLeaderPlacement(2, new Vector3(0f, 2f, 0f));

        TeamAnchorSession.NoteAuthoritativeRemoval(2);

        Assert.Equal(2, TeamAnchorSession.Count);
        TeamAnchorSession.NoteRemoved(2);
        Assert.Equal(1, TeamAnchorSession.Count);
        TeamAnchorSession.NoteRemoved(1);
        Assert.Equal(0, TeamAnchorSession.Count);
    }

    [Fact]
    public void TheFollowerDropsAHostIdFromTheChain()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(7, 4, 7, true);
        TeamAnchorSession.NoteRemotePlacement(4, 5, new Vector3(0f, 1f, 0f));
        TeamAnchorSession.NoteRemotePlacement(4, 6, new Vector3(0f, 2f, 0f));

        TeamAnchorSession.NoteAuthoritativeRemoval(5);

        Assert.Equal(1, TeamAnchorSession.Count);
    }

    [Fact]
    public void ChainIdsFollowThePitonsFromOldest()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        TeamAnchorSession.NoteLocalLeaderPlacement(1, new Vector3(0f, 1f, 0f));
        TeamAnchorSession.NoteLocalLeaderPlacement(2, new Vector3(0f, 2f, 0f));

        var ids = new List<uint>();
        var path = new List<Vector3>();
        Assert.True(TeamAnchorSession.CopyPath(path));
        TeamAnchorSession.CopyIds(ids);

        Assert.Equal(new uint[] { 1, 2 }, ids);
        Assert.Equal(ids.Count, path.Count);
    }

    [Fact]
    public void UntieQueuesEveryChainPitonSoNothingOnItCanCatch()
    {
        Reset();
        TeamAnchorSession.Bind(() => true);
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        TeamAnchorSession.NoteLocalLeaderPlacement(1, new Vector3(0f, 1f, 0f));
        TeamAnchorSession.NoteLocalLeaderPlacement(2, new Vector3(0f, 2f, 0f));

        RopeLinkState.Apply(4, 7, false);
        TeamAnchorSession.OnClip(4, 4, 7, false);

        var released = new List<TeamAnchorSession.ChainRelease>();
        TeamAnchorSession.CopyReleased(released);
        Assert.Equal(2, released.Count);
        Assert.Equal(1u, released[0].Id);
        Assert.True(released[0].LocalId);
        Assert.Equal(2u, released[1].Id);
        Assert.True(released[1].LocalId);
        Assert.False(TeamAnchorSession.HasProtection);
        Assert.False(TeamAnchorSession.AllowsPlacement);

        TeamAnchorSession.MarkReleased(released[0]);
        TeamAnchorSession.CopyReleased(released);
        var left = Assert.Single(released);
        Assert.Equal(2u, left.Id);
    }

    [Fact]
    public void UntieOnTheFollowerQueuesTheHostIds()
    {
        Reset();
        TeamAnchorSession.Bind(() => true);
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(7, 4, 7, true);
        TeamAnchorSession.NoteRemotePlacement(4, 5, new Vector3(0f, 1f, 0f));
        TeamAnchorSession.NoteRemotePlacement(4, 6, new Vector3(0f, 2f, 0f));

        RopeLinkState.Apply(4, 7, false);
        TeamAnchorSession.OnClip(7, 4, 7, false);

        var released = new List<TeamAnchorSession.ChainRelease>();
        TeamAnchorSession.CopyReleased(released);
        Assert.Equal(new uint[] { 5, 6 }, new[] { released[0].Id, released[1].Id });
        Assert.False(released[0].LocalId);
        Assert.False(released[1].LocalId);
    }

    private static void Reset()
    {
        RopeLinkState.Clear();
        TeamAnchorSession.Clear();
        TeamAnchorSession.DiscardRelease();
        TeamAnchorSession.Bind(() => false);
    }
}
