using System;
using System.Numerics;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayStationAnchorTests
{
    [Fact]
    public void SameHealthyStationSurvivesCharacterMovementAndMinorNativePrecision()
    {
        var station = new BelayStationAnchor(1, 2, 3, Vector3.Zero);
        Assert.True(station.Matches(1, 2, 3, 1, new Vector3(.01f, 0, 0)));
    }

    [Theory]
    [InlineData(9, 2, 3, 1)]
    [InlineData(1, 9, 3, 1)]
    [InlineData(1, 2, 9, 1)]
    [InlineData(1, 2, 3, 0)]
    [InlineData(1, 2, 3, -1)]
    [InlineData(1, 2, 3, float.NaN)]
    public void SceneRopePitonReplacementOrBreakInvalidatesTheStation(long lifeline, long rope, long piton, float health)
    {
        var station = new BelayStationAnchor(1, 2, 3, Vector3.Zero);
        Assert.False(station.Matches(lifeline, rope, piton, health, Vector3.Zero));
    }

    [Fact]
    public void MovedAndNonFiniteAnchorsAreRejected()
    {
        var station = new BelayStationAnchor(1, 2, 3, Vector3.Zero);
        Assert.False(station.Matches(1, 2, 3, 1, new Vector3(.051f, 0, 0)));
        Assert.False(station.Matches(1, 2, 3, 1, new Vector3(float.NaN, 0, 0)));
    }

    [Theory]
    [InlineData(0, 2, 3)]
    [InlineData(1, 0, 3)]
    [InlineData(1, 2, 0)]
    public void MissingIdentityCannotBeCaptured(long lifeline, long rope, long piton)
        => Assert.Throws<ArgumentException>(() => new BelayStationAnchor(lifeline, rope, piton, Vector3.Zero));
}
