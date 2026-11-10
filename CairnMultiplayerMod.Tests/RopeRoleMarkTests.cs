using System.Numerics;
using CairnMultiplayerMod.Internal.Game.Roping;
using CairnMultiplayerMod.Internal.Networking;
using Xunit;

namespace CairnMultiplayerMod.Tests;

[Collection("RopeTeamSession")]
public sealed class RopeRoleMarkTests
{
    [Fact]
    public void NobodyIsMarkedBeforeTheFirstClip()
    {
        Reset();

        Assert.False(Shown());
        Assert.Equal(RopeRoleMarkKind.None, Mark(4));
        Assert.Equal(RopeRoleMarkKind.None, Mark(7));
        Assert.False(RopeRoleMarkPolicy.Visible(4, 4, namesShown: true));
    }

    [Fact]
    public void TheClimberWhoClipsWearsTheCairn()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);

        Assert.True(Shown());
        Assert.Equal(RopeRoleMarkKind.Cairn, Mark(4));
        Assert.Equal(RopeRoleMarkKind.Stone, Mark(7));
        Assert.Equal(RopeRoleMarkKind.None, Mark(9));
    }

    [Fact]
    public void SwitchingWithThePracticeClimberMovesTheCairn()
    {
        Reset();
        RopeLinkState.Apply(4, PracticePartner.PlayerId, true);
        TeamAnchorSession.OnClip(4, 4, PracticePartner.PlayerId, true);

        Assert.True(TeamAnchorSession.ExchangePracticeRoles(PracticePartner.PlayerId));
        Assert.Equal(RopeRoleMarkKind.Cairn, Mark(PracticePartner.PlayerId));
        Assert.Equal(RopeRoleMarkKind.Stone, Mark(4));
    }

    [Fact]
    public void AWallUntieClearsBothMarks()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);

        RopeLinkState.Apply(4, 7, false);
        TeamAnchorSession.OnClip(4, 4, 7, false);

        Assert.False(TeamAnchorSession.IsLinked);
        Assert.False(Shown());
        Assert.Equal(RopeRoleMarkKind.None, Mark(4));
        Assert.Equal(RopeRoleMarkKind.None, Mark(7));
    }

    [Fact]
    public void LeavingAtAStaffKeepsBothMarks()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        TeamAnchorSession.NoteLocalLeaderPlacement(TopAnchorPolicy.FirstId, new Vector3(0f, 4f, 0f));
        TopAnchorSession.Remember(TopAnchorPolicy.FirstId, new Vector3(0f, 4f, 0f));
        TopAnchorSession.RequestHold();

        RopeLinkState.Apply(4, 7, false);
        TeamAnchorSession.OnClip(4, 4, 7, false);

        Assert.False(TeamAnchorSession.IsLinked);
        Assert.True(Shown());
        Assert.Equal(RopeRoleMarkKind.Cairn, Mark(4));
        Assert.Equal(RopeRoleMarkKind.Stone, Mark(7));
    }

    [Fact]
    public void CampClearsBothMarks()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        TopAnchorSession.RequestHold();
        RopeLinkState.Apply(4, 7, false);
        TeamAnchorSession.OnClip(4, 4, 7, false);
        Assert.True(Shown());

        TeamAnchorSession.Clear();
        TeamAnchorSession.DisarmRules();

        Assert.False(Shown());
        Assert.Equal(RopeRoleMarkKind.None, Mark(4));
        Assert.Equal(RopeRoleMarkKind.None, Mark(7));
    }

    [Fact]
    public void HidingNamesHidesTheMarks()
    {
        Assert.True(RopeRoleMarkPolicy.Visible(4, 7, namesShown: true));
        Assert.False(RopeRoleMarkPolicy.Visible(4, 7, namesShown: false));
        Assert.Equal(RopeRoleMarkKind.Cairn, RopeRoleMarkPolicy.For(4, 4, 7));
    }

    private static bool Shown()
        => RopeRoleMarkPolicy.Visible(
            TeamAnchorSession.LeaderId, TeamAnchorSession.FollowerId, namesShown: true);

    private static RopeRoleMarkKind Mark(int playerId)
        => RopeRoleMarkPolicy.For(playerId, TeamAnchorSession.LeaderId, TeamAnchorSession.FollowerId);

    private static void Reset()
    {
        PartnerFallSession.ForceRelease = false;
        RopeLinkState.Clear();
        TeamAnchorSession.Clear();
        TeamAnchorSession.DisarmRules();
        TeamAnchorSession.DiscardRelease();
        TeamAnchorSession.Bind(() => false);
    }
}
