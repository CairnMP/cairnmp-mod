using System;
using CairnMultiplayerMod.GameApi;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayDamageMathTests
{
    [Theory]
    [InlineData(6f, 4f, .6f)]
    [InlineData(4f, 2f, .95f)]
    [InlineData(8f, 2f, .95f)]
    [InlineData(.4f, .2f, 0f)]
    [InlineData(.5f, .25f, 0f)]
    [InlineData(1f, .5f, .0526239f)]
    public void CooperativeTargetKeepsTheSelectedHeightAdjustedCurve(float height, float length, float expected)
        => Assert.Equal(expected, BelayDamageMath.TargetFraction(height, length), 5);

    [Theory]
    [InlineData(2.445f, 5.381f, .0321f)]
    [InlineData(8.322f, 8.003f, .28837f)]
    public void PreviouslyMeasuredSoloCatchesKeepTheirTargetInCooperativeMode(float height, float length, float expected)
        => Assert.InRange(BelayDamageMath.TargetFraction(height, length), expected - .0002f, expected + .0002f);

    [Theory]
    [InlineData(float.NaN, 1f)]
    [InlineData(float.PositiveInfinity, 1f)]
    [InlineData(-1f, 1f)]
    [InlineData(1f, float.NaN)]
    [InlineData(1f, 0f)]
    [InlineData(1f, -1f)]
    [InlineData(1f, 201f)]
    public void InvalidMeasurementsNeverAuthorizeADose(float height, float length)
        => Assert.True(float.IsNaN(BelayDamageMath.TargetFraction(height, length)));

    [Fact]
    public void SelectedBelayerShareIsThirtyPercent()
        => Assert.Equal(.3f, BelayDamageMath.BelayerShare);
}
