using CairnMultiplayerMod.Internal.Game;
using CairnMultiplayerMod.Internal.Game.Life;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class SpectatorFlightTests
{
    [Fact]
    public void TheStickMatchesTheKeys()
    {
        var stick = SpectatorFlight.Combine(0f, 0f, 0f, false, stickX: 1f, stickY: 1f, rise: 0f, sink: 0f, stickFaster: false);
        var keys = SpectatorFlight.Combine(1f, 1f, 0f, false, 0f, 0f, 0f, 0f, false);

        Assert.Equal(keys.Forward, stick.Forward);
        Assert.Equal(keys.Strafe, stick.Strafe);
        Assert.False(stick.Faster);
    }

    [Fact]
    public void TheTriggersMatchRiseAndSink()
    {
        var rise = SpectatorFlight.Combine(0f, 0f, 0f, false, 0f, 0f, rise: 1f, sink: 0f, false);
        var space = SpectatorFlight.Combine(0f, 0f, 1f, false, 0f, 0f, 0f, 0f, false);
        var sink = SpectatorFlight.Combine(0f, 0f, 0f, false, 0f, 0f, rise: 0f, sink: 1f, false);
        var ctrl = SpectatorFlight.Combine(0f, 0f, -1f, false, 0f, 0f, 0f, 0f, false);

        Assert.Equal(space.Up, rise.Up);
        Assert.Equal(ctrl.Up, sink.Up);
    }

    [Fact]
    public void HoldingTheLeftStickGoesFasterTheWayShiftDoes()
    {
        var stick = SpectatorFlight.Combine(0f, 0f, 0f, keyFaster: false, 0f, 0f, 0f, 0f, stickFaster: true);
        var shift = SpectatorFlight.Combine(0f, 0f, 0f, keyFaster: true, 0f, 0f, 0f, 0f, stickFaster: false);

        Assert.True(stick.Faster);
        Assert.Equal(shift.Faster, stick.Faster);
    }

    [Fact]
    public void ARestingStickDoesNotDrift()
    {
        var idle = SpectatorFlight.Combine(0f, 0f, 0f, false, stickX: 0.1f, stickY: -0.1f, rise: 0.1f, sink: 0.1f, false);

        Assert.Equal(0f, idle.Forward);
        Assert.Equal(0f, idle.Strafe);
        Assert.Equal(0f, idle.Up);
    }
}

public sealed class ReviveHoldTests
{
    [Fact]
    public void SquareDoesNotCountWhileViewShareIsHeld()
    {
        Assert.True(ReviveHold.IsDown(keyHeld: false, squareHeld: true, viewShareHeld: false));
        Assert.False(ReviveHold.IsDown(keyHeld: false, squareHeld: true, viewShareHeld: true));
        Assert.True(ReviveHold.IsDown(keyHeld: true, squareHeld: false, viewShareHeld: true));
    }

    [Fact]
    public void TheCallWaitsForHalfASecondAndDoesNotRepeat()
    {
        var pressedAt = -1f;
        var fired = false;

        Assert.False(ButtonHold.Advance(ref pressedAt, ref fired, down: true, now: 1f, ReviveHold.Seconds));
        Assert.False(ButtonHold.Advance(ref pressedAt, ref fired, down: true, now: 1f + ReviveHold.Seconds - 0.01f, ReviveHold.Seconds));
        Assert.True(ButtonHold.Advance(ref pressedAt, ref fired, down: true, now: 1f + ReviveHold.Seconds, ReviveHold.Seconds));
        Assert.False(ButtonHold.Advance(ref pressedAt, ref fired, down: true, now: 2f, ReviveHold.Seconds));

        Assert.False(ButtonHold.Advance(ref pressedAt, ref fired, down: false, now: 3f, ReviveHold.Seconds));
        Assert.Equal(-1f, pressedAt);
        Assert.False(fired);
    }

    [Fact]
    public void LettingGoEarlyDoesNothing()
    {
        var pressedAt = -1f;
        var fired = false;

        Assert.False(ButtonHold.Advance(ref pressedAt, ref fired, down: true, now: 1f, ReviveHold.Seconds));
        Assert.False(ButtonHold.Advance(ref pressedAt, ref fired, down: false, now: 1.2f, ReviveHold.Seconds));
        Assert.False(fired);
    }
}
