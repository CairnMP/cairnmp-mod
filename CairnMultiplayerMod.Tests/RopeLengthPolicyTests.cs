using System.Numerics;
using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class RopeLengthPolicyTests
{
    [Fact]
    public void SlackAllowsAStepAwayFromThePartner()
    {
        var path = new[] { Vector3.Zero, new Vector3(2f, 0f, 0f) };

        Assert.False(RopeLengthPolicy.RefusesDirection(localIsLeader: true, path, 40f, new Vector3(1f, 0f, 0f)));
        Assert.False(RopeLengthPolicy.RefusesDirection(localIsLeader: false, path, 40f, new Vector3(-1f, 0f, 0f)));
    }

    [Fact]
    public void APaidOutRopeRefusesAStepAwayAndAllowsAStepBack()
    {
        var path = new[] { Vector3.Zero, new Vector3(40f, 0f, 0f) };

        Assert.True(RopeLengthPolicy.RefusesDirection(localIsLeader: true, path, 40f, new Vector3(1f, 0f, 0f)));
        Assert.False(RopeLengthPolicy.RefusesDirection(localIsLeader: true, path, 40f, new Vector3(-1f, 0f, 0f)));
        Assert.False(RopeLengthPolicy.RefusesDirection(localIsLeader: true, path, 40f, Vector3.Zero));
    }

    [Fact]
    public void LeaderAndFollowerUseOppositeEndsOfTheSamePath()
    {
        var path = new[] { Vector3.Zero, new Vector3(40f, 0f, 0f) };

        Assert.True(RopeLengthPolicy.RefusesDirection(localIsLeader: false, path, 40f, new Vector3(-1f, 0f, 0f)));
        Assert.False(RopeLengthPolicy.RefusesDirection(localIsLeader: false, path, 40f, new Vector3(1f, 0f, 0f)));
    }

    [Fact]
    public void APitonUsesUpTheCoilBeforeTheFreeEnd()
    {
        var straight = new[] { Vector3.Zero, new Vector3(5f, 0f, 0f) };
        Assert.False(RopeLengthPolicy.RefusesDirection(localIsLeader: true, straight, 40f, new Vector3(1f, 0f, 0f)));

        var slackPastThePiton = new[]
        {
            Vector3.Zero,
            new Vector3(35f, 0f, 0f),
            new Vector3(36f, 0f, 0f),
        };
        Assert.False(RopeLengthPolicy.RefusesDirection(
            localIsLeader: true, slackPastThePiton, 40f, new Vector3(1f, 0f, 0f)));

        var paidPastThePiton = new[]
        {
            Vector3.Zero,
            new Vector3(35f, 0f, 0f),
            new Vector3(40f, 0f, 0f),
        };
        Assert.True(RopeLengthPolicy.RefusesDirection(
            localIsLeader: true, paidPastThePiton, 40f, new Vector3(1f, 0f, 0f)));
        Assert.False(RopeLengthPolicy.RefusesDirection(
            localIsLeader: true, paidPastThePiton, 40f, new Vector3(-1f, 0f, 0f)));
    }

    [Fact]
    public void ANonFiniteValueDoesNotRefuse()
    {
        var path = new[] { Vector3.Zero, new Vector3(40f, 0f, 0f) };

        Assert.False(RopeLengthPolicy.RefusesDirection(true, path, float.NaN, new Vector3(1f, 0f, 0f)));
        Assert.False(RopeLengthPolicy.RefusesDirection(true, path, float.PositiveInfinity, new Vector3(1f, 0f, 0f)));
        Assert.False(RopeLengthPolicy.RefusesDirection(true, path, 40f, new Vector3(float.NaN, 0f, 0f)));

        var broken = new[] { Vector3.Zero, new Vector3(float.NaN, 0f, 0f) };
        Assert.False(RopeLengthPolicy.RefusesDirection(true, broken, 40f, new Vector3(1f, 0f, 0f)));
    }

    [Fact]
    public void TheWarningStaysHiddenWhileTheRopeHasSlack()
    {
        Assert.False(RopeLengthPolicy.ShowsAlmostOut(5.1f, 5f));
        Assert.False(RopeLengthPolicy.ShowsAlmostOut(float.NaN, 5f));
        Assert.False(RopeLengthPolicy.ShowsAlmostOut(-1f, 5f));
        Assert.False(RopeLengthPolicy.ShowsAlmostOut(1f, 0f));
        Assert.False(RopeLengthPolicy.ShowsAlmostOut(1f, float.NaN));
    }

    [Fact]
    public void TheWarningShowsOnceTheRopeIsAlmostOut()
    {
        Assert.True(RopeLengthPolicy.ShowsAlmostOut(5f, 5f));
        Assert.True(RopeLengthPolicy.ShowsAlmostOut(0f, 5f));
        Assert.Equal("3.25m", RopeLengthPolicy.WarningText(3.25f, "0.00", "m"));
        Assert.Equal("3.0 m", RopeLengthPolicy.WarningText(3f, "0.0", " m"));
        Assert.Null(RopeLengthPolicy.WarningText(float.NaN, "0.0", "m"));
        Assert.Equal("3.0m", RopeLengthPolicy.WarningText(3f, "not a format", "m"));
    }

    [Fact]
    public void TheNumberMovesTowardTheEndColor()
    {
        Assert.Equal(0f, RopeLengthPolicy.EndColorBlend(6f, 5f, 2f));
        Assert.Equal(0f, RopeLengthPolicy.EndColorBlend(2f, 5f, 2f));
        Assert.Equal(0.5f, RopeLengthPolicy.EndColorBlend(1f, 5f, 2f));
        Assert.Equal(1f, RopeLengthPolicy.EndColorBlend(0f, 5f, 2f));
        Assert.Equal(0.5f, RopeLengthPolicy.EndColorBlend(2.5f, 5f, 0f));
        Assert.Equal(0f, RopeLengthPolicy.EndColorBlend(float.NaN, 5f, 2f));
        Assert.True(RopeLengthPolicy.UsesOutline(2f, 2f));
        Assert.False(RopeLengthPolicy.UsesOutline(2.1f, 2f));
        Assert.False(RopeLengthPolicy.UsesOutline(1f, 0f));
    }

    [Fact]
    public void TheRopePictureFollowsTheMetresLeft()
    {
        var pictures = new[] { 0f, 2f, 4f };
        Assert.Equal(2, RopeLengthPolicy.RopePictureIndex(4.5f, pictures));
        Assert.Equal(1, RopeLengthPolicy.RopePictureIndex(3f, pictures));
        Assert.Equal(0, RopeLengthPolicy.RopePictureIndex(0f, pictures));
        Assert.Equal(0, RopeLengthPolicy.RopePictureIndex(0.5f, new[] { 1f, 3f }));
        Assert.Equal(2, RopeLengthPolicy.RopePictureIndex(3f, new[] { 4f, 0f, 2f }));
        Assert.Equal(-1, RopeLengthPolicy.RopePictureIndex(1f, null));
    }

    [Fact]
    public void AWorldPointIsTheStepFromTheHarness()
    {
        var here = new Vector3(-600f, -736f, -450f);
        var up = new Vector3(0f, 1f, 0f);
        Assert.Equal(up, RopeLengthPolicy.StepFromToward(up, here));

        var further = RopeLengthPolicy.StepFromToward(here + up, here);
        var back = RopeLengthPolicy.StepFromToward(here - up, here);
        var path = new[] { here - new Vector3(0f, 40f, 0f), here };

        Assert.Equal(up, further);
        Assert.True(RopeLengthPolicy.RefusesDirection(localIsLeader: true, path, 40f, further));
        Assert.False(RopeLengthPolicy.RefusesDirection(localIsLeader: true, path, 40f, back));
    }

    [Fact]
    public void AMissingMeasurementUsesTheLastAnswerAndAllowsTheFirstStep()
    {
        Assert.Null(RopeLengthPolicy.AnswerForStep(stillOnRope: false, measuredAllows: null, lastAllows: true));
        Assert.True(RopeLengthPolicy.AnswerForStep(stillOnRope: true, measuredAllows: null, lastAllows: null));
        Assert.True(RopeLengthPolicy.AnswerForStep(stillOnRope: true, measuredAllows: null, lastAllows: true));
        Assert.False(RopeLengthPolicy.AnswerForStep(stillOnRope: true, measuredAllows: null, lastAllows: false));
        Assert.False(RopeLengthPolicy.AnswerForStep(stillOnRope: true, measuredAllows: false, lastAllows: true));

        Assert.Null(RopeLengthPolicy.AnswerForRemaining(stillOnRope: false, measured: float.NaN, last: 12f));
        Assert.Equal(1f, RopeLengthPolicy.AnswerForRemaining(stillOnRope: true, measured: float.NaN, last: float.NaN));
        Assert.Equal(12f, RopeLengthPolicy.AnswerForRemaining(stillOnRope: true, measured: float.NaN, last: 12f));
        Assert.Equal(0f, RopeLengthPolicy.AnswerForRemaining(stillOnRope: true, measured: 0f, last: 12f));
    }

    [Fact]
    public void AKnownSpanAllowsTheStepUnlessTheTeamRopeRefusesIt()
    {
        Assert.Null(RopeLengthPolicy.StepAllowed(teamSpanKnown: false, teamRefuses: false));
        Assert.Null(RopeLengthPolicy.StepAllowed(teamSpanKnown: false, teamRefuses: true));
        Assert.True(RopeLengthPolicy.StepAllowed(teamSpanKnown: true, teamRefuses: false));
        Assert.False(RopeLengthPolicy.StepAllowed(teamSpanKnown: true, teamRefuses: true));
    }

    [Fact]
    public void TheTeamRemainderReplacesASpentNativeRope()
    {
        Assert.Equal(38f, RopeLengthPolicy.ReportedRemaining(nativeRemaining: 0f, teamRemaining: 38f));
        Assert.Equal(0f, RopeLengthPolicy.ReportedRemaining(nativeRemaining: 40f, teamRemaining: 0f));
        Assert.Equal(12f, RopeLengthPolicy.ReportedRemaining(nativeRemaining: 12f, teamRemaining: float.NaN));
    }

    [Fact]
    public void MetresLeftAreTheCoilMinusThePath()
    {
        Assert.Equal(28f, RopeLengthPolicy.RemainingMeters(12f, 40f));
        Assert.Equal(0f, RopeLengthPolicy.RemainingMeters(40f, 40f));
        Assert.Equal(0f, RopeLengthPolicy.RemainingMeters(47f, 40f));
        Assert.True(float.IsNaN(RopeLengthPolicy.RemainingMeters(float.NaN, 40f)));
        Assert.True(float.IsNaN(RopeLengthPolicy.RemainingMeters(12f, 0f)));
    }

    [Fact]
    public void TheLineIsTightOnlyWithinTheSkinOfTheCoil()
    {
        var coil = 40f;
        Assert.False(RopeLengthPolicy.IsPaidOut(coil - RopeLengthPolicy.SkinMeters - 0.01f, coil));
        Assert.True(RopeLengthPolicy.IsPaidOut(coil - RopeLengthPolicy.SkinMeters, coil));
        Assert.False(RopeLengthPolicy.IsPaidOut(float.NaN, coil));
        Assert.False(RopeLengthPolicy.IsPaidOut(coil, 0f));
    }

    [Fact]
    public void APaidOutWalkFacesAwayFromThePreviousPoint()
    {
        var path = new[] { Vector3.Zero, new Vector3(30f, 10f, 40f) };

        var leader = RopeLengthPolicy.WalkDirection(localIsLeader: true, path, 40f);
        var follower = RopeLengthPolicy.WalkDirection(localIsLeader: false, path, 40f);
        Assert.NotNull(leader);
        Assert.NotNull(follower);
        Assert.Equal(0f, leader.Value.Y);
        Assert.Equal(1f, leader.Value.Length(), 3);
        Assert.Equal(0.6f, leader.Value.X, 3);
        Assert.Equal(0.8f, leader.Value.Z, 3);
        Assert.Equal(-1f, Vector3.Dot(leader.Value, follower.Value), 3);

        var paidPastThePiton = new[]
        {
            Vector3.Zero,
            new Vector3(35f, 0f, 0f),
            new Vector3(35f, 0f, 40f),
        };
        var fromThePiton = RopeLengthPolicy.WalkDirection(localIsLeader: true, paidPastThePiton, 40f);
        Assert.NotNull(fromThePiton);
        Assert.Equal(0f, fromThePiton.Value.X, 3);
        Assert.Equal(0f, fromThePiton.Value.Y);
        Assert.Equal(1f, fromThePiton.Value.Z, 3);
    }

    [Fact]
    public void SlackAndAVerticalPairDoNotFaceAWalkWall()
    {
        var slack = new[] { Vector3.Zero, new Vector3(2f, 0f, 0f) };
        Assert.Null(RopeLengthPolicy.WalkDirection(localIsLeader: true, slack, 40f));

        var stacked = new[] { Vector3.Zero, new Vector3(0f, 40f, 0f) };
        Assert.True(RopeLengthPolicy.IsPaidOut(40f, 40f));
        Assert.Null(RopeLengthPolicy.WalkDirection(localIsLeader: true, stacked, 40f));
        Assert.Null(RopeLengthPolicy.WalkDirection(localIsLeader: false, stacked, 40f));

        var oneCentimetre = new[] { Vector3.Zero, new Vector3(0.01f, 40f, 0f) };
        Assert.Null(RopeLengthPolicy.WalkDirection(localIsLeader: true, oneCentimetre, 40f));
    }
}
