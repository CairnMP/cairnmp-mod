using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayInputLeaseTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void HeldMotionExpiresInFourHundredMillisecondsWithoutChangingLatchedBrake(int motion)
    {
        var session = Guid.NewGuid();
        var lease = new BelayInputLease(session, 0);
        Assert.True(lease.TryAccept(session, 1, true, motion, 1));
        Assert.Equal(motion, lease.MotionAt(1.399));
        Assert.Equal(0, lease.MotionAt(1.4));
        Assert.True(lease.Braked);
        Assert.False(lease.IsExpired(1.4));
        Assert.True(lease.IsExpired(4));
    }

    [Fact]
    public void DuplicateOldWrongSessionAndInvalidMotionCannotRenewHeldInput()
    {
        var session = Guid.NewGuid();
        var lease = new BelayInputLease(session, 0);
        Assert.True(lease.TryAccept(session, 2, true, -1, 1));
        Assert.False(lease.TryAccept(session, 2, false, 1, 1.3));
        Assert.False(lease.TryAccept(session, 1, false, 1, 1.3));
        Assert.False(lease.TryAccept(Guid.NewGuid(), 3, false, 1, 1.3));
        Assert.False(lease.TryAccept(session, 3, false, 2, 1.3));
        Assert.Equal(-1, lease.MotionAt(1.399));
        Assert.Equal(0, lease.MotionAt(1.4));
        Assert.Equal(2u, lease.Sequence);
        Assert.True(lease.Braked);
    }

    [Fact]
    public void CancelledRouteMotionNeedsAFreshIntentBeforeItCanResume()
    {
        var session = Guid.NewGuid();
        var lease = new BelayInputLease(session, 0);
        Assert.True(lease.TryAccept(session, 1, true, -1, 1));
        lease.CancelMotion();
        Assert.Equal(0, lease.MotionAt(1.1));
        Assert.False(lease.TryAccept(session, 1, false, 1, 1.1));
        Assert.Equal(0, lease.MotionAt(1.2));
        Assert.True(lease.Braked);
        Assert.True(lease.TryAccept(session, 2, true, -1, 1.3));
        Assert.Equal(-1, lease.MotionAt(1.3));
        Assert.True(lease.TryAccept(session, 3, true, 0, 1.4));
        Assert.Equal(0, lease.MotionAt(1.4));
    }

    [Fact]
    public void WrongClockOrExpiredPermissionNeverReplaysMotion()
    {
        var session = Guid.NewGuid();
        var lease = new BelayInputLease(session, 0);
        Assert.True(lease.TryAccept(session, 1, false, 1, 1));
        Assert.Equal(0, lease.MotionAt(.5));
        Assert.Equal(0, lease.MotionAt(double.NaN));
        Assert.Equal(0, lease.MotionAt(double.PositiveInfinity));
        Assert.False(lease.TryAccept(session, 2, false, 1, 4));
        Assert.Equal(0, lease.MotionAt(4));
    }

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
