using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class PitonPoolTests
{
    [Theory]
    [InlineData(4, 6, 10, 6)]
    [InlineData(8, 6, 10, 2)]
    [InlineData(10, 3, 10, 0)]
    [InlineData(0, 0, 10, 0)]
    [InlineData(3, 4, 0, 4)]
    [InlineData(-2, 5, 10, 5)]
    public void TheLeaderReceivesTheTotalUpToTheBagLimit(
        int leader, int follower, int max, int gain)
    {
        Assert.Equal(gain, PitonPool.FreshPitonsForLeader(leader, follower, max));
    }

    [Fact]
    public void AContributionRoundTripsAndRejectsACorruptCount()
    {
        var payload = PitonPool.WriteContribution(3, 7);
        Assert.True(PitonPool.TryReadContribution(payload, out var linkId, out var count));
        Assert.Equal(3, linkId);
        Assert.Equal(7, count);

        payload[4] = 0xFF;
        payload[5] = 0xFF;
        payload[6] = 0xFF;
        payload[7] = 0xFF;
        Assert.False(PitonPool.TryReadContribution(payload, out _, out _));
    }

    [Fact]
    public void ACountThatArrivesBeforeTheClipIsHeldUntilThisClimberIsTheLeader()
    {
        Assert.True(PitonPool.ShouldHold(isLeader: false));
        Assert.False(PitonPool.ShouldHold(isLeader: true));

        Assert.False(PitonPool.ShouldApply(
            isLeader: false, fromPlayerId: 7, followerId: 7, linkId: 1, tieId: 1, appliedTieId: 0));
        Assert.True(PitonPool.ShouldApply(
            isLeader: true, fromPlayerId: 7, followerId: 7, linkId: 1, tieId: 1, appliedTieId: 0));
        Assert.False(PitonPool.ShouldApply(
            isLeader: true, fromPlayerId: 7, followerId: 7, linkId: 1, tieId: 1, appliedTieId: 1));
        Assert.False(PitonPool.ShouldApply(
            isLeader: true, fromPlayerId: 4, followerId: 7, linkId: 1, tieId: 1, appliedTieId: 0));
        Assert.False(PitonPool.ShouldApply(
            isLeader: true, fromPlayerId: 7, followerId: 7, linkId: 1, tieId: 2, appliedTieId: 0));
    }
}
