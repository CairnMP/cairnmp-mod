using System.Collections.Generic;
using System.Numerics;
using System.Text;
using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

[Collection("RopeTeamSession")]
public sealed class CutsceneStaffTests
{
    private const uint Stick = TopAnchorPolicy.FirstId;

    [Fact]
    public void ASceneThatMovesHerOrEndsTheTieLeavesAStick()
    {
        Assert.Equal(CutsceneStaffChoice.Plant, Begin(endsTie: true, farMove: false));
        Assert.Equal(CutsceneStaffChoice.Plant, Begin(endsTie: false, farMove: true));
        Assert.Equal(CutsceneStaffChoice.Watch, Begin(endsTie: false, farMove: false));
    }

    [Fact]
    public void ADialogueSceneIsWatchedAndPlantsOnlyAfterSheLeaves()
    {
        var origin = new Vector3(10f, 2f, 4f);
        Assert.False(CutsceneStaffPolicy.Moved(origin, origin + new Vector3(5f, 0f, 0f)));
        Assert.True(CutsceneStaffPolicy.Moved(origin, origin + new Vector3(5.01f, 0f, 0f)));
        Assert.False(CutsceneStaffPolicy.Moved(origin, new Vector3(float.NaN, 2f, 4f)));
    }

    [Fact]
    public void AnyLiveStatePlantsAndADeathDoesNot()
    {
        // Standing, climbing, hanging, and falling are not inputs. The tie is.
        Assert.Equal(CutsceneStaffChoice.Plant, Begin(endsTie: false, farMove: true));
        Assert.Equal(CutsceneStaffChoice.Ignore, Begin(dead: true, endsTie: true, farMove: true));
        Assert.Equal(CutsceneStaffChoice.Ignore, Begin(tiedLeader: false, endsTie: true, farMove: true));
    }

    [Fact]
    public void TheFollowerRemovesOnlyTheMatchingScene()
    {
        Assert.Equal(CutsceneStaffChoice.Remove, Begin(tiedLeader: false, follower: true, openForKey: true));
        Assert.Equal(CutsceneStaffChoice.Ignore, Begin(tiedLeader: false, follower: true, openForKey: false));
        Assert.Equal(CutsceneStaffChoice.Ignore, Begin(openForKey: true));
        Assert.Equal(CutsceneStaffChoice.Ignore, Begin(hasKey: false, endsTie: true, farMove: true));
    }

    [Fact]
    public void TheSceneNameKeepsTheAreaAndFitsThePacket()
    {
        Assert.Null(CutsceneStaffPolicy.Key("Alps", "  "));
        Assert.Equal("Tenzen", CutsceneStaffPolicy.Key(null, " Tenzen "));
        Assert.Equal("Alps/Tenzen", CutsceneStaffPolicy.Key("Alps", "Tenzen"));

        var longName = new string('a', CutsceneStaffPolicy.MaxKeyBytes + 8);
        var key = CutsceneStaffPolicy.Key("Alps", longName);
        Assert.NotNull(key);
        Assert.True(Encoding.UTF8.GetByteCount(key) <= CutsceneStaffPolicy.MaxKeyBytes);
    }

    [Fact]
    public void StandingBesideAStoryStickDoesNotPullIt()
    {
        Reset();
        var lip = new Vector3(0f, 5f, 0f);
        var beside = new Vector3(3f, 5f, 0f);
        TopAnchorSession.Remember(Stick, lip);
        TopAnchorSession.RememberCutscene(Stick, new Vector3(0f, 0f, 1f), "Alps/Tenzen");

        Assert.True(TopAnchorSession.IsCutscene(Stick));
        Assert.True(TopAnchorSession.TryNearest(beside, out var any));
        Assert.Equal(Stick, any);
        Assert.False(TopAnchorSession.TryNearestMantle(beside, out _));
    }

    [Fact]
    public void AMantleStickStillComesOutWhenSheStandsThere()
    {
        Reset();
        var lip = new Vector3(0f, 5f, 0f);
        TopAnchorSession.Remember(Stick, lip);

        Assert.False(TopAnchorSession.IsCutscene(Stick));
        Assert.False(TopAnchorSession.HasCutsceneStick);
        Assert.True(TopAnchorSession.TryNearestMantle(new Vector3(3f, 5f, 0f), out var id));
        Assert.Equal(Stick, id);
    }

    [Fact]
    public void ASecondSceneCanLeaveItsOwnStick()
    {
        Reset();
        TopAnchorSession.Remember(Stick, new Vector3(0f, 1f, 0f));
        TopAnchorSession.RememberCutscene(Stick, Vector3.UnitY, "Alps/Tenzen");
        TopAnchorSession.Remember(Stick + 1, new Vector3(4f, 1f, 0f));
        TopAnchorSession.RememberCutscene(Stick + 1, Vector3.UnitY, "Alps/Marco");

        Assert.True(TopAnchorSession.HasKey("Alps/Tenzen"));
        Assert.True(TopAnchorSession.HasKey("Alps/Marco"));
        Assert.True(TopAnchorSession.TryIdForKey("Alps/Marco", out var id));
        Assert.Equal(Stick + 1, id);
    }

    [Fact]
    public void TheRopeMeetsTheHeadAlongTheFace()
    {
        var lip = new Vector3(1f, 2f, 3f);
        var tie = TopAnchorPolicy.TiePoint(lip, new Vector3(0f, 0f, 4f));
        Assert.Equal(lip + new Vector3(0f, 0f, TopAnchorPolicy.TieHeightMeters), tie);

        var upright = TopAnchorPolicy.TiePoint(lip, Vector3.Zero);
        Assert.Equal(TopAnchorPolicy.TiePoint(lip), upright);
        Assert.Equal(new Vector3(0f, 1f, 0f), TopAnchorPolicy.OutwardOrUp(new Vector3(float.NaN, 0f, 0f)));
    }

    [Fact]
    public void LoadingKeepsTheStickAndADeathOrCampDoesNot()
    {
        Assert.True(CutsceneStaffPolicy.SafetyKeepsStick(RopeSafetyAction.Continue));
        Assert.True(CutsceneStaffPolicy.SafetyKeepsStick(RopeSafetyAction.ReleaseAnchors));
        Assert.False(CutsceneStaffPolicy.SafetyKeepsStick(RopeSafetyAction.TearDown));
        Assert.False(CutsceneStaffPolicy.SafetyKeepsStick(RopeSafetyAction.UnclipOnce));

        Assert.True(CutsceneStaffPolicy.RestWaits(storyHold: true, campRest: false));
        Assert.False(CutsceneStaffPolicy.RestWaits(storyHold: true, campRest: true));
        Assert.False(CutsceneStaffPolicy.RestWaits(storyHold: false, campRest: false));
    }

    [Fact]
    public void TheStoryRestWaitsUntilSheIsBackOnTheMountain()
    {
        Reset();
        CutsceneStaffSession.ArmStory();
        CutsceneStaffSession.NoteGameplay(10f, inGame: true);
        CutsceneStaffSession.NoteGameplay(11.9f, inGame: true);
        Assert.True(CutsceneStaffSession.StoryHold);

        CutsceneStaffSession.NoteGameplay(12f, inGame: false);
        CutsceneStaffSession.NoteGameplay(12f, inGame: true);
        CutsceneStaffSession.NoteGameplay(13.9f, inGame: true);
        Assert.True(CutsceneStaffSession.StoryHold);
        CutsceneStaffSession.NoteGameplay(14f, inGame: true);
        Assert.False(CutsceneStaffSession.StoryHold);

        CutsceneStaffSession.ArmStory();
        CutsceneStaffSession.NoteCampRest();
        Assert.False(CutsceneStaffPolicy.RestWaits(CutsceneStaffSession.StoryHold, CutsceneStaffSession.TakeCampRest()));
        Assert.True(CutsceneStaffSession.StoryHold);
    }

    [Fact]
    public void LeavingTheMountainClearsTheStoryStick()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        TopAnchorSession.Remember(Stick, new Vector3(0f, 4f, 0f));
        TopAnchorSession.RememberCutscene(Stick, Vector3.UnitY, "Alps/Tenzen");
        CutsceneStaffSession.ArmStory();
        CutsceneStaffSession.BeginWatch("Alps/Tenzen", new Vector3(0f, 4f, 0f));

        TeamAnchorSession.Clear();

        Assert.False(TopAnchorSession.HasStick);
        Assert.False(TopAnchorSession.HasCutsceneStick);
        Assert.False(CutsceneStaffSession.StoryHold);
        Assert.False(CutsceneStaffSession.Watching);
        Assert.False(TeamAnchorSession.LeaderLeft);
    }

    [Fact]
    public void AStoryStaffEndsThePairAndRemovingItDoesNotTieThemAgain()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        TeamAnchorSession.NoteLocalLeaderPlacement(1, new Vector3(0f, 1f, 0f));
        TeamAnchorSession.NoteLocalLeaderPlacement(Stick, new Vector3(0f, 4f, 0f));
        TeamAnchorSession.NoteLocalLeaderPlacement(Stick + 1, new Vector3(8f, 4f, 0f));
        TopAnchorSession.Remember(Stick, new Vector3(0f, 4f, 0f));
        TopAnchorSession.RememberCutscene(Stick, Vector3.UnitY, "Alps/Tenzen");
        TopAnchorSession.Remember(Stick + 1, new Vector3(8f, 4f, 0f));
        TopAnchorSession.RememberCutscene(Stick + 1, Vector3.UnitY, "Alps/Marco");
        TeamAnchorSession.EndPartnership();

        Assert.False(TeamAnchorSession.IsLinked);
        Assert.True(TeamAnchorSession.RulesArmed);
        Assert.False(TeamAnchorSession.AllowsPlacement);
        Assert.True(TeamAnchorSession.Protects(1));
        Assert.True(TeamAnchorSession.Protects(Stick));
        AssertReleased();

        TopAnchorSession.Forget(Stick);
        TeamAnchorSession.NoteRemoved(Stick);
        TeamAnchorSession.RejoinIfStillTied();
        Assert.False(TeamAnchorSession.IsLinked);
        Assert.True(TeamAnchorSession.HasProtection);
        Assert.True(TeamAnchorSession.Protects(1));

        TopAnchorSession.Forget(Stick + 1);
        TeamAnchorSession.NoteRemoved(Stick + 1);
        TeamAnchorSession.RejoinIfStillTied();
        Assert.False(TeamAnchorSession.IsLinked);
        Assert.True(TeamAnchorSession.HasProtection);
        Assert.True(TeamAnchorSession.Protects(1));
        Assert.False(TeamAnchorSession.Protects(Stick));
        Assert.False(TeamAnchorSession.Protects(Stick + 1));
        AssertReleased();
    }

    [Fact]
    public void TheNextPressAfterAStoryStaffStartsANewLead()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        TeamAnchorSession.NoteLocalLeaderPlacement(1, new Vector3(0f, 1f, 0f));
        TeamAnchorSession.NoteLocalLeaderPlacement(Stick, new Vector3(0f, 4f, 0f));
        TopAnchorSession.Remember(Stick, new Vector3(0f, 4f, 0f));
        TopAnchorSession.RememberCutscene(Stick, Vector3.UnitY, "Alps/Tenzen");
        TeamAnchorSession.EndPartnership();

        RopeLinkState.Apply(7, 4, true);
        TeamAnchorSession.OnClip(7, 7, 4, true);

        Assert.True(TeamAnchorSession.IsLinked);
        Assert.Equal(7, TeamAnchorSession.LeaderId);
        Assert.Equal(4, TeamAnchorSession.FollowerId);
        Assert.Equal(0, TeamAnchorSession.Count);
        Assert.Equal(2, TeamAnchorSession.CatchCount);
        Assert.True(TeamAnchorSession.Protects(1));
        Assert.True(TeamAnchorSession.Protects(Stick));
        Assert.True(TeamAnchorSession.AllowsPlacement);
        AssertReleased();

        TopAnchorSession.Forget(Stick);
        TeamAnchorSession.NoteRemoved(Stick);
        TeamAnchorSession.RejoinIfStillTied();

        Assert.True(TeamAnchorSession.IsLinked);
        Assert.Equal(7, TeamAnchorSession.LeaderId);
        Assert.Equal(4, TeamAnchorSession.FollowerId);
        Assert.True(TeamAnchorSession.Protects(1));
        AssertReleased();
    }

    [Fact]
    public void AnUntieAtAMantleStickDoesNotTieThemAgain()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);
        TopAnchorSession.Remember(Stick, new Vector3(0f, 4f, 0f));
        TeamAnchorSession.NoteLocalLeaderPlacement(Stick, new Vector3(0f, 4f, 0f));
        TeamAnchorSession.EndPartnership();

        TeamAnchorSession.RejoinIfStillTied();

        Assert.False(TeamAnchorSession.IsLinked);
        Assert.False(TeamAnchorSession.LeaderLeft);
        Assert.False(RopeLinkState.IsLinked(4, 7));
        Assert.True(TeamAnchorSession.HasProtection);
    }

    [Fact]
    public void ASceneThatOnlyWatchesDoesNotEndThePair()
    {
        Reset();
        RopeLinkState.Apply(4, 7, true);
        TeamAnchorSession.OnClip(4, 4, 7, true);

        Assert.Equal(CutsceneStaffChoice.Watch, Begin(endsTie: false, farMove: false));
        Assert.True(TeamAnchorSession.IsLinked);
        Assert.Equal(4, TeamAnchorSession.LeaderId);
        Assert.Equal(7, TeamAnchorSession.FollowerId);
        Assert.True(RopeLinkState.IsLinked(4, 7));
    }

    [Fact]
    public void TheStoryStickTellsThePartnerTheScene()
    {
        var at = new Vector3(1.5f, 2.5f, 3.5f);
        var outward = new Vector3(0f, 0f, 1f);
        var payload = TopAnchorMessage.Cutscene(Stick, at, outward, "Alps/Tenzen");
        Assert.Equal(30 + Encoding.UTF8.GetByteCount("Alps/Tenzen"), payload.Length);
        Assert.True(TopAnchorMessage.TryRead(payload, out var packet));
        Assert.Equal(TopAnchorFact.Cutscene, packet.Fact);
        Assert.Equal(Stick, packet.Id);
        Assert.Equal(at, packet.Position);
        Assert.Equal(outward, packet.Outward);
        Assert.Equal("Alps/Tenzen", packet.Key);

        Assert.Equal(17, TopAnchorMessage.Place(Stick, at).Length);
        Assert.Null(TopAnchorMessage.Cutscene(0, at, outward, "Alps/Tenzen"));
        Assert.Null(TopAnchorMessage.Cutscene(Stick, at, outward, ""));
        Assert.Null(TopAnchorMessage.Cutscene(Stick, at, outward, new string('a', CutsceneStaffPolicy.MaxKeyBytes + 1)));

        var flat = TopAnchorMessage.Cutscene(Stick, at, Vector3.Zero, "Alps/Tenzen");
        Assert.True(TopAnchorMessage.TryRead(flat, out var stood));
        Assert.Equal(Vector3.Zero, stood.Outward);
        Assert.Equal(new Vector3(0f, 1f, 0f), TopAnchorPolicy.OutwardOrUp(stood.Outward));

        payload[17] = 0x00;
        payload[18] = 0x00;
        payload[19] = 0xC0;
        payload[20] = 0x7F;
        Assert.False(TopAnchorMessage.TryRead(payload, out _));
    }

    private static void AssertReleased()
    {
        var released = new List<TeamAnchorSession.ChainRelease>();
        TeamAnchorSession.CopyReleased(released);
        Assert.Empty(released);
    }

    private static CutsceneStaffChoice Begin(
        bool dead = false,
        bool tiedLeader = true,
        bool follower = false,
        bool endsTie = false,
        bool farMove = false,
        bool openForKey = false,
        bool hasKey = true)
        => CutsceneStaffPolicy.Begin(dead, tiedLeader, follower, endsTie, farMove, openForKey, hasKey);

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
