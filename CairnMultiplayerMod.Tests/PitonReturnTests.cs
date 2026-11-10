using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class PitonReturnTests
{
    [Fact]
    public void APitchRefusesTheHandover()
    {
        Assert.False(PitonReturn.AllowsHandover(pitchInForce: true));
        Assert.True(PitonReturn.AllowsHandover(pitchInForce: false));
    }

    [Fact]
    public void APocketIsRefusedUnlessATakeIsInProgress()
    {
        Assert.False(PitonReturn.AllowsPocket(pitchInForce: true, takeInProgress: false));
        Assert.True(PitonReturn.AllowsPocket(pitchInForce: true, takeInProgress: true));
        Assert.True(PitonReturn.AllowsPocket(pitchInForce: false, takeInProgress: false));
    }

    [Fact]
    public void APitchRefusesCairnsOwnRefill()
    {
        Assert.False(PitonReturn.AllowsNativeRefill(pitchInForce: true));
        Assert.True(PitonReturn.AllowsNativeRefill(pitchInForce: false));
    }

    [Fact]
    public void PitonsMoveWhenTheyRopeUpAfterTheRoute()
    {
        Assert.True(PitonReturn.ReturnsOnRopeUp(samePair: false, leaderLeft: false, staffStillHolds: false));
        Assert.True(PitonReturn.ReturnsOnRopeUp(samePair: true, leaderLeft: true, staffStillHolds: false));
        Assert.False(PitonReturn.ReturnsOnRopeUp(samePair: true, leaderLeft: true, staffStillHolds: true));
        Assert.False(PitonReturn.ReturnsOnRopeUp(samePair: true, leaderLeft: false, staffStillHolds: false));
    }

    [Fact]
    public void AFullBagStoresNoneOfAContribution()
    {
        var full = PitonPool.FreshPitonsForLeader(leaderCount: 6, followerCount: 6, leaderMax: 6);
        Assert.Equal(0, full);
        Assert.Equal(0, PitonReturn.RecordedGain(full, stored: 0));

        var asked = PitonPool.FreshPitonsForLeader(leaderCount: 0, followerCount: 6, leaderMax: 0);
        Assert.Equal(6, asked);
        Assert.Equal(1, PitonReturn.RecordedGain(asked, stored: 1));
        Assert.Equal(0, PitonPool.FreshPitonsForLeader(leaderCount: 6, followerCount: 1, leaderMax: 0));
    }
}
