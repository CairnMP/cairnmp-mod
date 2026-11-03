using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class PlayerInteractionPoseTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 3.01)]
    [InlineData(double.NaN, 2)]
    [InlineData(1, double.PositiveInfinity)]
    public void MissingOldFutureOrInvalidFrameTimesCannotSupplyAnInteraction(double captured, double now)
        => Assert.False(PlayerInteractionPose.TryRead(Frame(1, 2, 3), captured, now, out _));

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidBodyPointsCannotBecomePartnerPositions(float value)
        => Assert.False(PlayerInteractionPose.TryRead(Frame(value, 2, 3), 1, 1.1, out _));

    [Fact]
    public void PartialFramesAreUnavailableAndReturnedPointsDoNotBorrowTheirArray()
    {
        Assert.False(PlayerInteractionPose.TryRead(default, 1, 1.1, out _));
        Assert.False(PlayerInteractionPose.TryRead(new NetFrameData { IsValid = true, Positions = new float[] { 1, 2 } }, 1, 1.1, out _));
        var frame = Frame(1, 2, 3);
        Assert.True(PlayerInteractionPose.TryRead(frame, 1, 1.1, out var point));
        frame.Positions[0] = 99;
        Assert.Equal(1, point.X); Assert.Equal(2, point.Y); Assert.Equal(3, point.Z);
    }

    private static NetFrameData Frame(float x, float y, float z)
        => new() { IsValid = true, Positions = new[] { x, y, z } };
}
