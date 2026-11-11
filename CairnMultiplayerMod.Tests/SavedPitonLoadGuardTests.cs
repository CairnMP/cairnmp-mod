using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class SavedPitonLoadGuardTests
{
    [Theory]
    [InlineData(float.NaN, float.NaN, 0f)]
    [InlineData(float.NaN, 12.5f, -3f)]
    [InlineData(12.5f, float.NaN, -3f)]
    [InlineData(12.5f, 40f, float.NaN)]
    [InlineData(float.PositiveInfinity, 40f, -3f)]
    [InlineData(12.5f, float.NegativeInfinity, -3f)]
    [InlineData(12.5f, 40f, float.PositiveInfinity)]
    public void APitonWithoutAFinitePositionIsHollow(float x, float y, float z)
        => Assert.True(SavedPitonLoadGuard.IsHollow(x, y, z));

    [Theory]
    [InlineData(12.5f, 40f, -3f)]
    [InlineData(-1820.25f, 963.5f, 412.75f)]
    [InlineData(12.5f, 40f, 0f)]
    [InlineData(0f, 40f, -3f)]
    [InlineData(0f, 0f, 0f)]
    [InlineData(float.MaxValue, float.MinValue, float.Epsilon)]
    public void APitonWithARealPositionIsKept(float x, float y, float z)
        => Assert.False(SavedPitonLoadGuard.IsHollow(x, y, z));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public void TheSweepIsOwedOnceWhenTheWorldStarts(bool wasStarted, bool isStarted, bool owed)
        => Assert.Equal(owed, SavedPitonLoadGuard.LoadJustFinished(wasStarted, isStarted));

    [Fact]
    public void ACleanPassIsNotRepeated()
        => Assert.False(SavedPitonLoadGuard.ShouldRetry(failed: 0, passes: 1));

    [Fact]
    public void AFailedPassIsRetriedUntilTheLimit()
    {
        Assert.True(SavedPitonLoadGuard.ShouldRetry(failed: 2, passes: 1));
        Assert.True(SavedPitonLoadGuard.ShouldRetry(failed: 1, passes: SavedPitonLoadGuard.MaxPasses - 1));
        Assert.False(SavedPitonLoadGuard.ShouldRetry(failed: 1, passes: SavedPitonLoadGuard.MaxPasses));
    }
}
