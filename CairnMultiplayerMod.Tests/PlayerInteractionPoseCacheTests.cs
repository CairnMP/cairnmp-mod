using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class PlayerInteractionPoseCacheTests
{
    [Fact]
    public void ALongFrameRefreshesTheLocalBodyBeforeHostValidation()
    {
        var cache = new PlayerInteractionPoseCache();
        cache.Remember(Frame(1), 10);
        var now = 12.51;
        Assert.True(cache.TryGet(() => now, 100, () => cache.Remember(Frame(7), now), out var position));
        Assert.Equal(7, position.X);
    }

    [Fact]
    public void AFreshPoseDoesNotTriggerAnotherNativeCapture()
    {
        var cache = new PlayerInteractionPoseCache();
        cache.Remember(Frame(3), 10);
        var refreshes = 0;
        Assert.True(cache.TryGet(() => 10.1, 100, () => refreshes++, out var position));
        Assert.Equal(3, position.X); Assert.Equal(0, refreshes);
    }

    [Fact]
    public void FailedNativeRefreshIsBoundedPerFrameAndCanRetryOnTheNextFrame()
    {
        var cache = new PlayerInteractionPoseCache(); var refreshes = 0;
        Assert.False(cache.TryGet(() => 10, 100, () => refreshes++, out _));
        Assert.False(cache.TryGet(() => 10, 100, () => refreshes++, out _));
        Assert.Equal(1, refreshes);
        Assert.True(cache.TryGet(() => 10.1, 101, () => { refreshes++; cache.Remember(Frame(4), 10.1); }, out _));
        Assert.Equal(2, refreshes);
    }

    [Fact]
    public void CaptureDelayUsesTheNewCaptureTimeRatherThanTheExpiredQueryTime()
    {
        var cache = new PlayerInteractionPoseCache(); var now = 10d;
        cache.Remember(Frame(1), 1);
        Assert.True(cache.TryGet(() => now, 100, () => { now += 2.6; cache.Remember(Frame(2), now); }, out var position));
        Assert.Equal(2, position.X);
    }

    [Fact]
    public void InvalidCaptureCannotReviveAnExpiredBodyPoint()
    {
        var cache = new PlayerInteractionPoseCache();
        cache.Remember(Frame(1), 1);
        Assert.False(cache.TryGet(() => 4, 100, () => cache.Remember(Frame(float.NaN), 4), out _));
    }

    [Fact]
    public void NoActiveGameplayCaptureKeepsThePositionUnavailable()
    {
        var cache = new PlayerInteractionPoseCache();
        cache.Remember(Frame(1), 1);
        Assert.False(cache.TryGet(() => 4, 100, () => { }, out _));
    }

    [Fact]
    public void SceneResetClearsBothThePoseAndTheRefreshAttempt()
    {
        var cache = new PlayerInteractionPoseCache();
        Assert.True(cache.TryGet(() => 10, 100, () => cache.Remember(Frame(1), 10), out _));
        cache.Reset();
        Assert.True(cache.TryGet(() => 10.1, 100, () => cache.Remember(Frame(2), 10.1), out var position));
        Assert.Equal(2, position.X);
    }

    private static NetFrameData Frame(float x)
        => new() { IsValid = true, Positions = new[] { x, 2f, 3f } };
}
