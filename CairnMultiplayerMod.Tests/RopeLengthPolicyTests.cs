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
    public void TheLineIsTightOnlyWithinTheSkinOfTheCoil()
    {
        var coil = 40f;
        Assert.False(RopeLengthPolicy.IsPaidOut(coil - RopeLengthPolicy.SkinMeters - 0.01f, coil));
        Assert.True(RopeLengthPolicy.IsPaidOut(coil - RopeLengthPolicy.SkinMeters, coil));
        Assert.False(RopeLengthPolicy.IsPaidOut(float.NaN, coil));
        Assert.False(RopeLengthPolicy.IsPaidOut(coil, 0f));
    }
}
