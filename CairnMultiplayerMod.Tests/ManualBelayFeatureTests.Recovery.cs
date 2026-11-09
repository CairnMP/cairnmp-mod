using System;
using System.Linq;
using CairnMultiplayer.Api;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.GameApi;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed partial class ManualBelayFeatureTests
{
    private static World ConfirmedRecoveryPair(int belayer = 1, int climber = 2)
    {
        var world = new World();
        world.Nodes[belayer].Game.ChatImpl.Run("offer " + climber);
        world.Nodes[climber].Game.ChatImpl.Run("accept");
        world.Tick(); Advance(world, .6f);
        return world;
    }

    private static ManualBelayMessage InterruptRecoveryPair(World world, int player = 2)
    {
        world.Nodes[player].Game.Keys.Focused = false;
        Advance(world, .11f);
        return world.Last(ManualBelayPhase.Interrupted);
    }

    private static void PumpRecovery(World world, int steps = 15)
    { for (var i = 0; i < steps; i++) Advance(world, .11f); }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 3)]
    public void AConfirmedPairResumesWithFreshNativeChecksANewSessionAndClosedBrake(int belayer, int climber)
    {
        using var world = ConfirmedRecoveryPair(belayer, climber);
        var original = world.Last(ManualBelayPhase.Started);
        var previousRope = world.Nodes[climber].Game.BelayImpl.Rope;
        if (belayer != 1 && climber != 1) world.Nodes[1].Game.BelayImpl.RecoveryAlive = false; // Relay host is not a participant.
        var interrupted = InterruptRecoveryPair(world, climber);
        Assert.Equal(original.Session, interrupted.Session);
        Assert.NotEqual(Guid.Empty, interrupted.Recovery);
        Assert.False(world.Nodes[climber].Game.BelayImpl.Active);
        Assert.Contains("PARTNER BRAKE OFF", world.Nodes[climber].Game.HudImpl.Messages["manual-belay.belay-role"]);
        world.Nodes[climber].Game.Keys.Focused = true;
        PumpRecovery(world);
        var resumed = world.Last(ManualBelayPhase.Resumed);
        Assert.NotEqual(original.Session, resumed.Session);
        Assert.Equal(interrupted.Recovery, resumed.Recovery);
        Assert.Equal(belayer, resumed.Belayer); Assert.Equal(climber, resumed.Climber);
        Assert.True(resumed.Braked); Assert.Equal(0, resumed.Motion);
        Assert.True(world.Nodes[climber].Game.BelayImpl.Active);
        Assert.True(world.Nodes[climber].Game.BelayImpl.LastBrake);
        Assert.NotSame(previousRope, world.Nodes[climber].Game.BelayImpl.Rope);
        Assert.Equal(0, world.Nodes[climber].Game.BelayImpl.Rope.Motion);
        Assert.Equal(1, world.Messages.Count(m => m.Phase == ManualBelayPhase.Offered));
        Assert.False(world.Execute(belayer, new() { Operation = ManualBelayOperation.Intent,
            Session = original.Session, Sequence = 999, Braked = false, Motion = 1 }).Committed);
        Assert.Empty(world.Failures);
    }

    [Fact]
    public void PatchRetentionIsExplicitlyBoundToRecoveryAndReleasedAfterResumeOrEnd()
    {
        using var world = ConfirmedRecoveryPair();
        var original = world.Last(ManualBelayPhase.Started);
        InterruptRecoveryPair(world);
        foreach (var node in world.Nodes.Values.Take(2))
            Assert.Equal(original.Session, node.Game.BelayImpl.RetainedPatchSession);
        world.Nodes[2].Game.Keys.Focused = true;
        PumpRecovery(world);
        foreach (var node in world.Nodes.Values.Take(2))
            Assert.Equal(Guid.Empty, node.Game.BelayImpl.RetainedPatchSession);
        var next = world.Last(ManualBelayPhase.Resumed);
        InterruptRecoveryPair(world);
        Assert.Equal(next.Session, world.Nodes[2].Game.BelayImpl.RetainedPatchSession);
        world.Nodes[1].Game.ChatImpl.Run("stop");
        PumpRecovery(world);
        foreach (var node in world.Nodes.Values.Take(2))
        {
            Assert.Equal(Guid.Empty, node.Game.BelayImpl.RetainedPatchSession);
            Assert.Equal("release", node.Game.BelayImpl.RecoveryPatchCalls.Last());
        }
    }

    [Fact]
    public void FirstAttachmentWithoutConfirmedControlDoesNotRetainConsent()
    {
        using var world = new World();
        world.Nodes[2].Game.BelayImpl.NeedsAttachment = true;
        world.Nodes[1].Game.ChatImpl.Run("offer 2"); world.Nodes[2].Game.ChatImpl.Run("accept"); world.Tick();
        world.Nodes[2].Game.Keys.Focused = false; Advance(world, .11f);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.Interrupted);
        world.Nodes[2].Game.Keys.Focused = true; PumpRecovery(world);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.Resumed);
    }

    [Fact]
    public void EndWhileTheHostResumeCommitIsInFlightCancelsTheNewSessionAndLateCommit()
    {
        using var world = ConfirmedRecoveryPair();
        var interrupted = InterruptRecoveryPair(world);
        world.DeferredEventRecipient = 2; world.DeferredPhase = ManualBelayPhase.Resumed;
        world.Nodes[2].Game.Keys.Focused = true;
        for (var i = 0; i < 15 && world.DeferredEvents.Count == 0; i++) Advance(world, .11f);
        Assert.NotEmpty(world.DeferredEvents);
        var begins = world.Nodes[2].Game.BelayImpl.BeginCalls;
        world.Nodes[2].Game.ChatImpl.Run("stop");
        world.FlushEvents(); PumpRecovery(world);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        Assert.Equal(begins, world.Nodes[2].Game.BelayImpl.BeginCalls);
        Assert.DoesNotContain("manual-belay.belay-role", world.Nodes[2].Game.HudImpl.Messages.Keys);
        Assert.False(world.Execute(2, new() { Operation = ManualBelayOperation.ResumeReady,
            Session = interrupted.Session, Recovery = interrupted.Recovery, Station = interrupted.Station,
            RopeCapacity = interrupted.RopeCapacity, LengthApplied = true }).Committed);
    }

    [Fact]
    public void DuplicateInterruptedMessagesKeepTheCurrentReadinessAndDeadline()
    {
        using var world = ConfirmedRecoveryPair();
        InterruptRecoveryPair(world);
        Advance(world, .11f); // Belayer sends a fresh ready; climber remains unfocused.
        var packet = world.Events.Last(e => Decode(e).Phase == ManualBelayPhase.Interrupted);
        world.Nodes[1].Runtime.ApplyEvent(packet);
        world.Nodes[2].Runtime.ApplyEvent(packet);
        world.Nodes[2].Game.Keys.Focused = true;
        PumpRecovery(world);
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        Assert.Single(world.Messages, m => m.Phase == ManualBelayPhase.Resumed);
        Assert.Empty(world.Failures);
    }

    [Fact]
    public void LostReadinessAndMissingStationWaitWithoutReusingTheOldPositiveReady()
    {
        using var world = ConfirmedRecoveryPair();
        InterruptRecoveryPair(world);
        Advance(world, .11f); // Host/belayer ready first.
        world.Nodes[1].Game.Keys.Focused = false;
        world.Nodes[2].Game.Keys.Focused = true;
        world.Nodes[2].Game.BelayImpl.ResumeClimberReady = false;
        PumpRecovery(world, 20);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.Resumed);
        world.Nodes[2].Game.BelayImpl.ResumeClimberReady = true;
        PumpRecovery(world, 20);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.Resumed);
        world.Nodes[1].Game.Keys.Focused = true;
        PumpRecovery(world);
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
    }

    [Fact]
    public void ExplicitEndAndExpiredInputCannotBeResurrectedByOldEvents()
    {
        using var world = ConfirmedRecoveryPair();
        var started = world.Events.Last(e => Decode(e).Phase == ManualBelayPhase.Started);
        world.Time += 3.1f; // No Tick: the old input lease expired before the explicit stop reached the host.
        world.Nodes[2].Game.ChatImpl.Run("stop");
        Assert.Equal(ManualBelayPhase.Ended, world.Messages.Last().Phase);
        world.Nodes[2].Runtime.ApplyEvent(started); PumpRecovery(world);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.Resumed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DeathSceneResetParticipantLeaveAndRecoveryDeadlineRequireANewInvitation(int ending)
    {
        using var world = ConfirmedRecoveryPair();
        InterruptRecoveryPair(world);
        if (ending == 0) world.Nodes[2].Game.BelayImpl.RecoveryAlive = false;
        else if (ending == 1) world.Nodes[2].Host.NotifySceneReset();
        else if (ending == 2) world.Nodes[1].Runtime.NotifyPlayerLeft(new MultiplayerPlayer(2, "P2", false, false));
        else world.Time += 121;
        world.Tick(); world.Nodes[2].Game.Keys.Focused = true; PumpRecovery(world);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.Resumed);
    }

    [Fact]
    public void ARecoveryCannotUseAnOutsiderWrongNonceStationOrCapacity()
    {
        using var world = ConfirmedRecoveryPair();
        var interrupted = InterruptRecoveryPair(world);
        ManualBelayCommand Ready() => new() { Operation = ManualBelayOperation.ResumeReady,
            Session = interrupted.Session, Recovery = interrupted.Recovery, Station = interrupted.Station,
            RopeCapacity = interrupted.RopeCapacity, LengthApplied = true };
        Assert.False(world.Execute(3, Ready()).Committed);
        var wrong = Ready(); wrong.Recovery = Guid.NewGuid(); Assert.False(world.Execute(2, wrong).Committed);
        wrong = Ready(); wrong.Station = wrong.Station with { Id = Guid.NewGuid() }; Assert.False(world.Execute(2, wrong).Committed);
        wrong = Ready(); wrong.RopeCapacity--; Assert.False(world.Execute(2, wrong).Committed);
        Assert.False(world.Execute(1, new() { Operation = ManualBelayOperation.Offer, Target = 3,
            Station = interrupted.Station }).Committed);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.Resumed);
    }

    [Fact]
    public void FiveRecoveriesExhaustTheLineageAndANewInvitationCannotBeCancelledByItsOldAlias()
    {
        using var world = ConfirmedRecoveryPair();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            InterruptRecoveryPair(world);
            world.Nodes[2].Game.Keys.Focused = true;
            PumpRecovery(world);
            Assert.True(world.Nodes[2].Game.BelayImpl.Active);
            Assert.Equal(attempt, world.Messages.Count(m => m.Phase == ManualBelayPhase.Resumed));
        }
        var oldInterrupted = world.Last(ManualBelayPhase.Interrupted);
        var oldPacket = world.Events.Last(e => Decode(e).Phase == ManualBelayPhase.Interrupted);
        var begins = world.Nodes[2].Game.BelayImpl.BeginCalls;
        world.Nodes[2].Game.Keys.Focused = false; Advance(world, .11f);
        Assert.Equal(ManualBelayPhase.Ended, world.Messages.Last().Phase);
        world.Nodes[2].Game.Keys.Focused = true;
        world.Nodes[2].Runtime.ApplyEvent(oldPacket); PumpRecovery(world);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        Assert.Equal(begins, world.Nodes[2].Game.BelayImpl.BeginCalls);
        Assert.Equal(5, world.Messages.Count(m => m.Phase == ManualBelayPhase.Resumed));

        world.Nodes[1].Game.ChatImpl.Run("offer 2"); world.Nodes[2].Game.ChatImpl.Run("accept");
        world.Tick(); Advance(world, .6f);
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        Assert.False(world.Execute(2, new() { Operation = ManualBelayOperation.Stop,
            Session = oldInterrupted.Session, Recovery = oldInterrupted.Recovery }).Committed);
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        InterruptRecoveryPair(world);
        world.Nodes[2].Game.Keys.Focused = true; PumpRecovery(world);
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        Assert.Equal(6, world.Messages.Count(m => m.Phase == ManualBelayPhase.Resumed));
        Assert.Equal(2, world.Messages.Count(m => m.Phase == ManualBelayPhase.Offered));
    }

    [Fact]
    public void HeldRopeKeysMustBeReleasedAfterResumeBeforeNewInputCanAct()
    {
        using var world = ConfirmedRecoveryPair();
        InterruptRecoveryPair(world);
        world.Nodes[1].Game.Keys.Up = true;
        world.Nodes[2].Game.Keys.Focused = true;
        PumpRecovery(world);
        Assert.True(world.Nodes[2].Game.BelayImpl.LastBrake);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.Rope.Motion);
        world.Nodes[1].Game.Keys.Up = false; Advance(world, .11f);
        world.Nodes[1].Game.Keys.Pressed = true; Advance(world, .11f);
        world.Nodes[1].Game.Keys.Up = true; Advance(world, .11f);
        Assert.False(world.Nodes[2].Game.BelayImpl.LastBrake);
        Assert.Equal(1, world.Nodes[2].Game.BelayImpl.Rope.Motion);
    }
}
