using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayInputLeaseTests
{
    [Fact]
    public void ExplicitIntentDoesNotToggleOnRetransmissionAndOnlyNewSequencesRenewLease()
    {
        var id = Guid.NewGuid();
        var lease = new BelayInputLease(id, 10);
        Assert.True(lease.TryAccept(id, 1, true, 11));
        Assert.False(lease.TryAccept(id, 1, false, 13));
        Assert.True(lease.Braked);
        Assert.False(lease.IsExpired(13.999));
        Assert.True(lease.IsExpired(14));
        Assert.False(lease.TryAccept(id, 2, false, 14));
        Assert.True(lease.Braked);
    }

    [Fact]
    public void WrongSessionOldSequenceAndBackwardTimeCannotChangeTheBrake()
    {
        var id = Guid.NewGuid();
        var lease = new BelayInputLease(id, 0);
        Assert.True(lease.TryAccept(id, 2, true, 1));
        Assert.False(lease.TryAccept(Guid.NewGuid(), 3, false, 2));
        Assert.False(lease.TryAccept(id, 1, false, 2));
        Assert.False(lease.TryAccept(id, 3, false, .5));
        Assert.True(lease.Braked);
        Assert.True(lease.TryAccept(id, 3, false, 2));
        Assert.False(lease.Braked);
        Assert.True(lease.IsExpired(5));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidClockCannotGrantOrRenewPermission(double now)
    {
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentOutOfRangeException>(() => new BelayInputLease(id, now));
        var lease = new BelayInputLease(id, 0);
        Assert.False(lease.TryAccept(id, 1, false, now));
        Assert.True(lease.IsExpired(now));
    }

    [Fact]
    public void ANewSessionCannotConsumeAReplayFromThePreviousPairing()
    {
        var first = Guid.NewGuid();
        var next = Guid.NewGuid();
        var lease = new BelayInputLease(next, 10);
        Assert.False(lease.TryAccept(first, uint.MaxValue, false, 10));
        Assert.True(lease.TryAccept(next, 1, true, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BelayInputLease(Guid.Empty, 0));
    }
}
