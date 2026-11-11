using System.Collections.Generic;
using System.Numerics;
using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

[Collection("RopeTeamSession")]
public sealed class RopeDisconnectTests
{
    private const uint Stick = TopAnchorPolicy.FirstId;

    [Fact]
    public void APartnerLeavingClearsTheChainEvenWhenAStaffIsHoldingThePitch()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(7, 4, 7, true);
        TeamAnchorSession.NoteRemotePlacement(4, 1, new Vector3(0f, 1f, 0f));
        TeamAnchorSession.NoteRemotePlacement(4, Stick, new Vector3(0f, 4f, 0f));
        TopAnchorSession.Remember(Stick, new Vector3(0f, 4f, 0f));
        TopAnchorSession.RequestHold();
        TeamAnchorSession.HoldForFollower();
        RopeLinkState.Apply(4, 7, false);

        Assert.True(TeamAnchorSession.LeaderLeft);
        Assert.True(RopeDisconnect.Untie(7, 4, localHanging: false, whileTied: null));

        Assert.False(TeamAnchorSession.IsLinked);
        Assert.False(TeamAnchorSession.LeaderLeft);
        Assert.False(TeamAnchorSession.HasProtection);
        Assert.False(TopAnchorSession.HasStick);
        Assert.False(PartnerFallSession.DropPending);
        var released = new List<TeamAnchorSession.ChainRelease>();
        TeamAnchorSession.CopyReleased(released);
        var only = Assert.Single(released);
        Assert.Equal(1u, only.Id);
    }

    [Fact]
    public void APartnerLeavingWhileThisClimberHangsArmsTheDropBeforeTheLinkIsRemoved()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);

        var stillTied = false;
        var armedWhileTied = false;
        Assert.True(RopeDisconnect.Untie(4, 7, localHanging: true, () =>
        {
            stillTied = RopeLinkState.IsLinked(4, 7) && TeamAnchorSession.IsLinked;
            armedWhileTied = PartnerFallSession.DropPending;
        }));

        Assert.True(stillTied);
        Assert.True(armedWhileTied);
        Assert.False(RopeLinkState.IsLinked(4, 7));
        Assert.False(TeamAnchorSession.IsLinked);
        Assert.True(PartnerFallSession.DropPending);
    }

    [Fact]
    public void SomeoneElseLeavingLeavesTheTieAlone()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);

        Assert.False(RopeDisconnect.Untie(4, 9, localHanging: true, whileTied: null));

        Assert.True(RopeLinkState.IsLinked(4, 7));
        Assert.True(TeamAnchorSession.IsLinked);
        Assert.Equal(4, TeamAnchorSession.LeaderId);
        Assert.Equal(7, TeamAnchorSession.FollowerId);
        Assert.False(PartnerFallSession.DropPending);
    }

    [Fact]
    public void EndingTheSessionUntiesBeforeTheRopeIsReleased()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        var steps = new List<string>();

        RopeDisconnect.EndSession(
            4,
            new[] { 7 },
            localHanging: true,
            _ => steps.Add(PartnerFallSession.DropPending ? "untie" : "untie-without-drop"),
            () => steps.Add("release"));

        Assert.Equal(new[] { "untie", "release" }, steps);
        Assert.False(TeamAnchorSession.IsLinked);
        Assert.False(RopeLinkState.IsLinked(4, 7));
        Assert.True(PartnerFallSession.DropPending);
    }

    private static void Reset()
    {
        RopeLinkState.Clear();
        TeamAnchorSession.Clear();
        TeamAnchorSession.DisarmRules();
        TeamAnchorSession.DiscardRelease();
        TeamAnchorSession.Bind(() => true);
        PartnerFallSession.Reset();
    }
}
