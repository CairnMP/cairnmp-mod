using System.Collections.Generic;
using System.Numerics;
using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

[Collection("RopeTeamSession")]
public sealed class TopAnchorTests
{
    private const uint Stick = TopAnchorPolicy.FirstId;

    [Fact]
    public void TheLedgeVolumeIsEightMetersAcrossAndOpenUpward()
    {
        var stick = new Vector3(0f, 10f, 0f);

        Assert.True(TopAnchorPolicy.InVolume(stick, new Vector3(8f, 10f, 0f)));
        Assert.False(TopAnchorPolicy.InVolume(stick, new Vector3(8.01f, 10f, 0f)));
        Assert.True(TopAnchorPolicy.InVolume(stick, new Vector3(0f, 8f, 0f)));
        Assert.False(TopAnchorPolicy.InVolume(stick, new Vector3(0f, 7.99f, 0f)));
        Assert.True(TopAnchorPolicy.InVolume(stick, new Vector3(0f, 1000f, 0f)));
        Assert.False(TopAnchorPolicy.InVolume(stick, new Vector3(float.NaN, 10f, 0f)));
    }

    [Fact]
    public void TheShaftSeatsInTheLipAndABoundsHangDoesNot()
    {
        Assert.True(TopAnchorPolicy.TrySeatLift(10f, 8.9f, out var lift));
        Assert.Equal(1.02f, lift, 3);

        Assert.True(TopAnchorPolicy.TrySeatLift(10f, 10.2f, out var above));
        Assert.Equal(0f, above);

        // The glow's lowest corner was 2.05 m down and still a legal seat. A 4 m hang is not the stick.
        Assert.True(TopAnchorPolicy.TrySeatLift(10f, 7.95f, out var glow));
        Assert.Equal(1.97f, glow, 2);
        Assert.False(TopAnchorPolicy.TrySeatLift(10f, 6f, out _));
        Assert.False(TopAnchorPolicy.TrySeatLift(10f, float.NaN, out _));
    }

    [Fact]
    public void AMantleOntoTheLedgeCommitsAndAFallDoesNot()
    {
        var watch = new TopAnchorMantle();
        var lip = new Vector3(1f, 2f, 3f);

        Assert.False(watch.Observe(TopAnchorModule.Mantling, walking: false, lip, out _));
        Assert.True(watch.Armed);

        Assert.True(watch.Observe(TopAnchorModule.LedgeWalking, walking: false, lip, out var committed));
        Assert.Equal(lip, committed);

        watch = new TopAnchorMantle();
        watch.Observe(TopAnchorModule.Mantling, false, lip, out _);
        Assert.False(watch.Observe(TopAnchorModule.Falling, false, lip, out _));
        Assert.False(watch.Armed);

        watch = new TopAnchorMantle();
        watch.Observe(TopAnchorModule.Mantling, false, lip, out _);
        Assert.True(watch.Observe(TopAnchorModule.Other, walking: true, lip, out _));
    }

    [Fact]
    public void ModuleNamesMapOntoTheMantleDecision()
    {
        Assert.Equal(TopAnchorModule.Mantling, TopAnchorPolicy.Classify("ClimbingV2PawnControllerModuleMantlingV2"));
        Assert.Equal(TopAnchorModule.SecuredFalling, TopAnchorPolicy.Classify("ClimbingV2PawnControllerModuleSecuredFalling"));
        Assert.Equal(TopAnchorModule.Other, TopAnchorPolicy.Classify("ClimbingV2PawnControllerModuleIdle"));
        Assert.Equal(TopAnchorModule.Wall, TopAnchorPolicy.Classify("ClimbingV2PawnControllerModuleReaching"));
        Assert.Equal(TopAnchorModule.GetDownFromWall, TopAnchorPolicy.Classify("ClimbingV2PawnControllerModuleGetDownFromWall"));
    }

    [Fact]
    public void AMantleThatLeavesThroughIdleCommitsWhenThePawnWalks()
    {
        var watch = new TopAnchorMantle();
        var lip = new Vector3(1f, 2f, 3f);
        watch.Observe(TopAnchorModule.Mantling, walking: false, lip, out _);
        Assert.False(watch.Observe(TopAnchorModule.Other, walking: false, lip, out _));
        Assert.True(watch.Armed);

        Assert.True(watch.Observe(TopAnchorModule.Other, walking: true, lip, out var committed));
        Assert.Equal(lip, committed);

        watch = new TopAnchorMantle();
        watch.Observe(TopAnchorModule.Mantling, false, lip, out _);
        watch.Observe(TopAnchorModule.Other, false, lip, out _);
        Assert.False(watch.Observe(TopAnchorModule.Wall, walking: false, lip, out _));
        Assert.False(watch.Armed);
    }

    [Fact]
    public void ThePlantPointIsWhereTheMantleFinishes()
    {
        var watch = new TopAnchorMantle();
        var start = new Vector3(0f, 1f, 0f);
        var stood = new Vector3(0.4f, 2.2f, 0.1f);
        watch.Observe(TopAnchorModule.Mantling, walking: false, start, out _);
        Assert.False(watch.Observe(TopAnchorModule.Other, walking: false, stood, out _));
        Assert.True(watch.Armed);
        Assert.True(watch.Observe(TopAnchorModule.Other, walking: true, stood, out var committed));
        Assert.Equal(stood, committed);
    }

    [Fact]
    public void FactsRoundTrip()
    {
        var place = new Vector3(1.5f, 2.5f, 3.5f);
        Assert.True(TopAnchorMessage.TryRead(TopAnchorMessage.Place(Stick, place), out var placed));
        Assert.Equal(TopAnchorFact.Place, placed.Fact);
        Assert.Equal(Stick, placed.Id);
        Assert.Equal(place, placed.Position);

        Assert.True(TopAnchorMessage.TryRead(TopAnchorMessage.Clean(Stick), out var cleaned));
        Assert.Equal(Stick, cleaned.Id);

        Assert.True(TopAnchorMessage.TryRead(TopAnchorMessage.Topped(place), out var topped));
        Assert.Equal(place, topped.Position);

        Assert.True(TopAnchorMessage.TryRead(TopAnchorMessage.Leave(new uint[] { Stick, Stick + 1 }), out var left));
        Assert.Equal(new uint[] { Stick, Stick + 1 }, left.LeaveIds);

        Assert.True(TopAnchorMessage.TryRead(TopAnchorMessage.Drop(), out var drop));
        Assert.Equal(TopAnchorFact.Drop, drop.Fact);
        Assert.False(TopAnchorMessage.TryRead(new byte[] { 1, 0 }, out _));
    }

    [Fact]
    public void AStickBelowTheFollowerBecomesTheirAnchorWhenTheyTakeThePitonUnderIt()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(7, 4, 7, true);
        TeamAnchorSession.NoteRemotePlacement(4, 1, new Vector3(0f, 1f, 0f));
        TeamAnchorSession.NoteRemotePlacement(4, Stick, new Vector3(0f, 4f, 0f));
        TopAnchorSession.Remember(Stick, new Vector3(0f, 4f, 0f));

        Assert.Equal(1u, FollowerAnchor());
        Assert.False(TeamAnchorSession.AllowsTake(Stick));
        Assert.True(TeamAnchorSession.AllowsTake(1));

        TeamAnchorSession.NoteRemoved(1);

        Assert.Equal(Stick, FollowerAnchor());
        Assert.True(TeamAnchorSession.HasProtection);
    }

    [Fact]
    public void TheOnlyStickAnchorsTheFollowerImmediately()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(7, 4, 7, true);
        TeamAnchorSession.NoteRemotePlacement(4, Stick, new Vector3(0f, 4f, 0f));

        Assert.Equal(Stick, FollowerAnchor());
    }

    [Fact]
    public void ASecondStickInsideTheVolumeIsRefusedByTheNearestTest()
    {
        Reset();
        var lip = new Vector3(0f, 5f, 0f);
        TopAnchorSession.Remember(Stick, lip);

        Assert.True(TopAnchorSession.TryNearest(new Vector3(3f, 5f, 0f), out var id));
        Assert.Equal(Stick, id);
        Assert.False(TopAnchorSession.TryNearest(new Vector3(30f, 5f, 0f), out _));
    }

    [Fact]
    public void ACleanedStickIgnoresALatePlace()
    {
        Reset();
        TopAnchorSession.Remember(Stick, new Vector3(0f, 1f, 0f));
        TopAnchorSession.Forget(Stick);

        Assert.True(TopAnchorSession.WasCleaned(Stick));
        TopAnchorSession.Remember(Stick, new Vector3(0f, 2f, 0f));
        Assert.False(TopAnchorSession.HasStick);
    }

    [Fact]
    public void TheLeaderCanLeaveAfterAStickWithoutClearingTheChain()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        TeamAnchorSession.NoteLocalLeaderPlacement(1, new Vector3(0f, 1f, 0f));
        TeamAnchorSession.NoteLocalLeaderPlacement(Stick, new Vector3(0f, 4f, 0f));
        TopAnchorSession.Remember(Stick, new Vector3(0f, 4f, 0f));
        TopAnchorSession.RequestHold();

        RopeLinkState.Apply(4, 7, false);
        TeamAnchorSession.OnClip(4, 4, 7, false);
        TeamAnchorSession.ReconcileLink();

        Assert.True(TeamAnchorSession.LeaderLeft);
        Assert.True(TeamAnchorSession.IsLinked);
        Assert.True(TeamAnchorSession.RulesArmed);
        Assert.Equal(2, TeamAnchorSession.Count);
        Assert.Equal(1u, FollowerAnchor());
        Assert.Equal(Stick, LeaderAnchor());
        Assert.False(TeamAnchorSession.AllowsPlacement);

        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);

        Assert.False(TeamAnchorSession.LeaderLeft);
        Assert.Equal(2, TeamAnchorSession.Count);
    }

    [Fact]
    public void TheStaffBecomesTheFollowerAnchorOnlyAfterTheOtherPitonsAreGone()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        TeamAnchorSession.NoteLocalLeaderPlacement(1, new Vector3(0f, 1f, 0f));
        TeamAnchorSession.NoteLocalLeaderPlacement(2, new Vector3(0f, 2f, 0f));
        TeamAnchorSession.NoteLocalLeaderPlacement(Stick, new Vector3(0f, 4f, 0f));
        TopAnchorSession.Remember(Stick, new Vector3(0f, 4f, 0f));
        TopAnchorSession.RequestHold();

        RopeLinkState.Apply(4, 7, false);
        TeamAnchorSession.OnClip(4, 4, 7, false);

        Assert.Equal(3, TeamAnchorSession.Count);
        Assert.Equal(1u, FollowerAnchor());
        Assert.Equal(Stick, LeaderAnchor());

        TeamAnchorSession.NoteRemoved(1);
        Assert.Equal(2u, FollowerAnchor());
        Assert.Equal(Stick, LeaderAnchor());

        TeamAnchorSession.NoteRemoved(2);
        Assert.Equal(Stick, FollowerAnchor());
        Assert.Equal(Stick, LeaderAnchor());
    }

    [Fact]
    public void ALateLeaveDoesNotKeepTheNextUntieFromClearing()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        TeamAnchorSession.NoteLocalLeaderPlacement(1, new Vector3(0f, 1f, 0f));
        TeamAnchorSession.NoteLocalLeaderPlacement(Stick, new Vector3(0f, 4f, 0f));
        TopAnchorSession.Remember(Stick, new Vector3(0f, 4f, 0f));

        RopeLinkState.Apply(4, 7, false);
        TeamAnchorSession.OnClip(4, 4, 7, false);
        TopAnchorSession.RequestHold();

        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        TopAnchorSession.Forget(Stick);
        TeamAnchorSession.NoteRemoved(Stick);

        RopeLinkState.Apply(4, 7, false);
        TeamAnchorSession.OnClip(4, 4, 7, false);

        Assert.False(TeamAnchorSession.IsLinked);
        Assert.False(TeamAnchorSession.HasProtection);
        Assert.False(TopAnchorSession.HasStick);
    }

    [Fact]
    public void ALeaderUnclipWithNoStickStillClearsTheChain()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        TeamAnchorSession.NoteLocalLeaderPlacement(1, new Vector3(0f, 1f, 0f));

        RopeLinkState.Apply(4, 7, false);
        TeamAnchorSession.OnClip(4, 4, 7, false);

        Assert.False(TeamAnchorSession.IsLinked);
        Assert.False(TeamAnchorSession.HasProtection);
    }

    [Fact]
    public void ADropClearsAChainThatHasAStick()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        TeamAnchorSession.NoteLocalLeaderPlacement(Stick, new Vector3(0f, 4f, 0f));
        TopAnchorSession.Remember(Stick, new Vector3(0f, 4f, 0f));
        TopAnchorSession.RequestDrop();

        RopeLinkState.Apply(4, 7, false);
        TeamAnchorSession.OnClip(4, 4, 7, false);

        Assert.False(TeamAnchorSession.IsLinked);
        Assert.False(TeamAnchorSession.HasProtection);
        Assert.False(TopAnchorSession.HasStick);
    }

    [Fact]
    public void TheFollowersUnclipClearsAHeldChainAndDoesNotQueueTheStick()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        TeamAnchorSession.NoteLocalLeaderPlacement(1, new Vector3(0f, 1f, 0f));
        TeamAnchorSession.NoteLocalLeaderPlacement(Stick, new Vector3(0f, 4f, 0f));
        TopAnchorSession.Remember(Stick, new Vector3(0f, 4f, 0f));
        TopAnchorSession.RequestHold();
        RopeLinkState.Apply(4, 7, false);
        TeamAnchorSession.OnClip(4, 4, 7, false);

        TeamAnchorSession.OnClip(4, 7, 4, false);

        Assert.False(TeamAnchorSession.HasProtection);
        var released = new List<TeamAnchorSession.ChainRelease>();
        TeamAnchorSession.CopyReleased(released);
        var only = Assert.Single(released);
        Assert.Equal(1u, only.Id);
    }

    private static uint FollowerAnchor()
    {
        Assert.True(TeamAnchorSession.TryGetFollowerAnchor(out var id, out _));
        return id;
    }

    private static uint LeaderAnchor()
    {
        Assert.True(TeamAnchorSession.TryGetLeaderAnchor(out var id, out _));
        return id;
    }

    private static void Reset()
    {
        RopeLinkState.Clear();
        TeamAnchorSession.Clear();
        TeamAnchorSession.DisarmRules();
        TeamAnchorSession.DiscardRelease();
        TeamAnchorSession.Bind(() => true);
    }
}
