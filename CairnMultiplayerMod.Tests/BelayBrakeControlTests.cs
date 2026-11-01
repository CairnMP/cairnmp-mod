using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayBrakeControlTests
{
    [Fact]
    public void ManualStartsFreeAndPaysOutToTheRopeEnd()
    {
        var control = new BelayBrakeControl(37);
        Assert.True(control.TryAttach(.8f, .8f, out var target));
        Assert.Equal(37, target);
        Assert.False(control.Braked);
        foreach (var distance in new[] { 1f, 4f, 20f, 37.1f })
        {
            Assert.True(control.TryStep(distance, out target));
            Assert.Equal(37, target);
        }
    }

    [Fact]
    public void BrakeCapturesPhysicalDistanceOnceAndStaysLatchedUntilReleased()
    {
        var control = new BelayBrakeControl(37);
        Assert.True(control.TryAttach(.8f, .8f, out _));
        control.ToggleBrake();
        Assert.True(control.Braked);
        Assert.True(control.TryStep(2, out var target));
        Assert.Equal(2.03f, target, 5);
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(control.TryStep(i % 2 == 0 ? 1 : 2.2f, out target));
            Assert.Equal(2.03f, target, 5);
        }
        control.ToggleBrake();
        Assert.False(control.Braked);
        Assert.True(control.TryStep(2, out target));
        Assert.Equal(37, target);
        control.ToggleBrake();
        Assert.True(control.TryStep(4, out target));
        Assert.Equal(4.03f, target, 5);
    }

    [Fact]
    public void BrakeSelectedWhileClimbingCapturesAtSuspensionCreation()
    {
        var control = new BelayBrakeControl(37);
        control.ToggleBrake();
        Assert.True(control.TryAttach(.8f, .9f, out var target));
        Assert.Equal(.93f, target, 5);
        Assert.True(control.Braked);
    }

    [Fact]
    public void ReleasingBeforeSuspensionDoesNotLeaveAnOldCapturePending()
    {
        var control = new BelayBrakeControl(37);
        control.ToggleBrake();
        control.ToggleBrake();
        Assert.True(control.TryAttach(.8f, .8f, out var target));
        Assert.Equal(37, target);
    }

    [Fact]
    public void RopeEndCapsBrakeDistanceEvenWhenPhysicalRopeIsStretched()
    {
        var control = new BelayBrakeControl(2);
        Assert.True(control.TryAttach(.5f, .5f, out _));
        control.ToggleBrake();
        Assert.True(control.TryStep(2.2f, out var target));
        Assert.Equal(2, target);
    }

    [Theory]
    [InlineData(float.NaN, 1f)]
    [InlineData(.5f, float.PositiveInfinity)]
    [InlineData(0f, .5f)]
    [InlineData(.5f, 0f)]
    [InlineData(38f, .5f)]
    public void InvalidAttachmentDoesNotConsumeTheSession(float native, float real)
    {
        var control = new BelayBrakeControl(37);
        Assert.False(control.TryAttach(native, real, out var unchanged));
        Assert.Equal(native, unchanged);
        Assert.False(control.Attached);
        Assert.True(control.TryAttach(.5f, .5f, out _));
        Assert.False(control.TryAttach(.5f, .5f, out _));
    }

    [Fact]
    public void InvalidReadbackDoesNotConsumePendingBrakeCapture()
    {
        var control = new BelayBrakeControl(37);
        Assert.False(control.TryStep(.5f, out _));
        Assert.True(control.TryAttach(.5f, .5f, out _));
        control.ToggleBrake();
        Assert.False(control.TryStep(float.NaN, out _));
        Assert.True(control.CaptureBrakeDistance);
        Assert.True(control.TryStep(2, out var target));
        Assert.Equal(2.03f, target, 5);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(201f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidCapacityCannotCreateAController(float available)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BelayBrakeControl(available));
    }
}
