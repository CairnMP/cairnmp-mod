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
    public void TheTestReadoutShowsThePathAgainstTheCoil()
    {
        Assert.Equal("Rope 12.50 / 40.00 m", RopeLengthPolicy.Readout(12.5f, 40f));
        Assert.Equal("Rope 40.00 / 40.00 m — paid out", RopeLengthPolicy.Readout(40f, 40f));
        Assert.Null(RopeLengthPolicy.Readout(float.NaN, 40f));
        Assert.Null(RopeLengthPolicy.Readout(12f, 0f));

        var slack = RopeLengthPolicy.Readout(40f - RopeLengthPolicy.SkinMeters - 0.02f, 40f);
        Assert.StartsWith("Rope ", slack);
        Assert.DoesNotContain("paid out", slack);
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
}
