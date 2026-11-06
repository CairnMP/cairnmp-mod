using System;
using System.IO;
using System.Linq;
using CairnMultiplayer.Api;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed partial class ManualBelayFeatureTests
{
    private static World DamageWorld(int belayer = 1, int climber = 2, float start = 1)
    {
        var world = new World { Time = start };
        foreach (var node in world.Nodes.Values) node.Game.BelayImpl.SharedDamageEnabled = true;
        world.Nodes[belayer].Game.ChatImpl.Run("offer " + climber);
        world.Nodes[climber].Game.ChatImpl.Run("accept"); world.Tick();
        return world;
    }

    private static void DamageTicks(World world, int count = 3, float step = .1f)
    { for (var i = 0; i < count; i++) { world.Time += step; world.Tick(); } }

    private static ManualBelayDamageClaim Claim(World world, uint fall = 1, float native = 0)
        => new() { Session = world.Last(ManualBelayPhase.Started).Session, Fall = fall,
            Height = 6, Length = 4, NativeLossFraction = native };

    private static CommandResult DamageCommand(World world, int sender, string command, IPacket value)
        => world.Nodes[1].Runtime.ExecuteHostCommand(sender, new ClientExtensionCommand {
            RequestId = 800, ExtensionId = FeatureHost.CoreExtensionId,
            CommandId = "manual-belay.damage-" + command, Payload = Bytes(value) });

    private static ManualBelayDamageMessage DecodeDamage(ServerExtensionEvent packet)
    {
        var message = new ManualBelayDamageMessage();
        using var reader = new BinaryReader(new MemoryStream(packet.Payload)); message.Deserialize(reader); return message;
    }

    private static ServerExtensionEvent DamageEvent(World world, ManualBelayDamagePhase phase)
        => world.Events.Last(e => e.EventId == "manual-belay.damage-approved" && DecodeDamage(e).Phase == phase);

    [Fact]
    public void AcceptedFallClaimsCannotAuthorizeAnotherDoseBeforeOneSecond()
    {
        using var world = DamageWorld();
        var first = Claim(world);
        Assert.True(DamageCommand(world, 2, "claim", first).Committed);
        Assert.True(DamageCommand(world, 2, "receipt", new ManualBelayDamageReceipt {
            Session = first.Session, Fall = 1, Completed = true, ObservedFraction = .6f }).Committed);
        var next = Claim(world, 2);
        Assert.False(DamageCommand(world, 2, "claim", next).Committed);
        world.Time = 1.999f;
        Assert.False(DamageCommand(world, 2, "claim", next).Committed);
        world.Time = 2;
        Assert.True(DamageCommand(world, 2, "claim", next).Committed);
        Assert.Equal(2, world.Events.Count(e => e.EventId == "manual-belay.damage-approved"
            && DecodeDamage(e).Phase == ManualBelayDamagePhase.ClimberApproval));
    }

    [Fact]
    public void TheFirstClaimAtClockZeroIsAcceptedAndAnOutsiderCannotConsumeItsSlot()
    {
        using var world = DamageWorld(start: 0);
        var first = Claim(world);
        Assert.False(DamageCommand(world, 3, "claim", first).Committed);
        Assert.True(DamageCommand(world, 2, "claim", first).Committed);
        Assert.Equal(0, world.Time);
        Assert.Empty(world.Nodes[2].Game.BelayImpl.DamageRequests);
    }

    [Fact]
    public void AReversedClaimClockIsRejectedWithoutMakingTheNextSequenceReplayable()
    {
        using var world = DamageWorld();
        var first = Claim(world);
        Assert.True(DamageCommand(world, 2, "claim", first).Committed);
        Assert.True(DamageCommand(world, 2, "receipt", new ManualBelayDamageReceipt {
            Session = first.Session, Fall = 1, Completed = false }).Committed);
        world.Time = .5f;
        var reversed = DamageCommand(world, 2, "claim", Claim(world, 2));
        Assert.False(reversed.Committed); Assert.Contains("clock reversed", reversed.Reason);
        world.Time = 2;
        Assert.True(DamageCommand(world, 2, "claim", Claim(world, 2)).Committed);
        Assert.False(DamageCommand(world, 2, "claim", Claim(world, 2)).Committed);
    }

    [Fact]
    public void ANewSessionStartsItsOwnCadenceAndFallSequence()
    {
        using var world = DamageWorld();
        var old = Claim(world);
        Assert.True(DamageCommand(world, 2, "claim", old).Committed);
        world.Nodes[1].Game.ChatImpl.Run("stop");
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        world.Nodes[2].Game.ChatImpl.Run("accept");
        var fresh = Claim(world);
        Assert.NotEqual(old.Session, fresh.Session);
        Assert.Equal(1, world.Time);
        Assert.False(DamageCommand(world, 2, "claim", old).Committed);
        Assert.True(DamageCommand(world, 2, "claim", fresh).Committed);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void TheOwnerAppliesOnceAndTheOtherRoleReceivesThirtyPercentOfActualImpact(int belayer, int climber)
    {
        using var world = DamageWorld(belayer, climber);
        var state = world.Last(ManualBelayPhase.Started);
        var falling = world.Nodes[climber].Game.BelayImpl;
        var holding = world.Nodes[belayer].Game.BelayImpl;
        falling.Hp = 80; // Native fall loss 10 plus unrelated consumption 10; only the former is credited.
        holding.MaxHp = holding.Hp = 200;
        falling.PartnerFall = new(state.Session, 1, 6, 4, .1f);
        DamageTicks(world, 1);
        Assert.Empty(falling.DamageRequests); // Approval alone has not claimed a native application.
        Assert.Empty(holding.DamageRequests);
        DamageTicks(world);
        var own = Assert.Single(falling.DamageRequests);
        var share = Assert.Single(holding.DamageRequests);
        Assert.False(own.Belayer); Assert.True(share.Belayer);
        Assert.Equal(state.Session, own.Session); Assert.Equal(1u, own.Fall);
        Assert.Equal(.5f, own.Fraction, 4); Assert.Equal(.18f, share.Fraction, 4);
        Assert.Equal(30, falling.Hp, 4); Assert.Equal(164, holding.Hp, 4);
        Assert.Equal(1, falling.NativeDamageCalls); Assert.Equal(1, holding.NativeDamageCalls);
        Assert.Empty(world.Nodes[3].Game.BelayImpl.DamageRequests);
        Assert.Empty(world.Failures);
    }

    [Fact]
    public void ShieldReductionSharesTheObservedAdditionRatherThanTheRequestedAddition()
    {
        using var world = DamageWorld();
        var climber = world.Nodes[2].Game.BelayImpl;
        climber.DamageScale = .5f;
        climber.PartnerFall = new(Claim(world).Session, 1, 6, 4, .1f);
        DamageTicks(world, 4);
        Assert.Equal(.5f, Assert.Single(climber.DamageRequests).Fraction, 4);
        Assert.Equal(.105f, Assert.Single(world.Nodes[1].Game.BelayImpl.DamageRequests).Fraction, 4);
    }

    [Theory]
    [InlineData(.7f, .21f)]
    [InlineData(1f, .3f)]
    public void SufficientNativeCreditConsumesAZeroAdditionWithoutAnotherNativeClimberCall(float native, float share)
    {
        using var world = DamageWorld();
        var climber = world.Nodes[2].Game.BelayImpl;
        climber.PartnerFall = new(Claim(world).Session, 1, 6, 4, native);
        DamageTicks(world, 4);
        Assert.Equal(0, Assert.Single(climber.DamageRequests).Fraction);
        Assert.Equal(0, climber.NativeDamageCalls);
        Assert.Null(climber.PartnerFall);
        Assert.Equal(share, Assert.Single(world.Nodes[1].Game.BelayImpl.DamageRequests).Fraction, 4);
    }

    [Fact]
    public void AZeroHeightDampedTargetDoesNotInvokeEitherNativeDamageCall()
    {
        using var world = DamageWorld();
        world.Nodes[2].Game.BelayImpl.PartnerFall = new(Claim(world).Session, 1, .4f, .2f, 0);
        DamageTicks(world, 4);
        foreach (var id in new[] { 1, 2 })
        {
            Assert.Equal(0, Assert.Single(world.Nodes[id].Game.BelayImpl.DamageRequests).Fraction);
            Assert.Equal(0, world.Nodes[id].Game.BelayImpl.NativeDamageCalls);
        }
    }

    [Fact]
    public void ReplayedApprovalsDosesAndRepeatedPollingCannotRepeatEitherNativeCall()
    {
        using var world = DamageWorld();
        var observation = new PartnerFallObservation(Claim(world).Session, 1, 6, 4, 0);
        world.Nodes[2].Game.BelayImpl.PartnerFall = observation;
        DamageTicks(world, 4);
        var approval = DamageEvent(world, ManualBelayDamagePhase.ClimberApproval);
        var dose = DamageEvent(world, ManualBelayDamagePhase.BelayerDose);
        for (var i = 0; i < 4; i++)
        {
            world.Nodes[2].Game.BelayImpl.PartnerFall = observation;
            foreach (var node in world.Nodes.Values) { node.Runtime.ApplyEvent(approval); node.Runtime.ApplyEvent(dose); }
            DamageTicks(world, 1);
        }
        Assert.Single(world.Nodes[2].Game.BelayImpl.DamageRequests);
        Assert.Single(world.Nodes[1].Game.BelayImpl.DamageRequests);
        Assert.False(DamageCommand(world, 2, "claim", Claim(world)).Committed);
        Assert.False(DamageCommand(world, 2, "receipt", new ManualBelayDamageReceipt {
            Session = observation.Session, Fall = 1, Completed = true, ObservedFraction = .6f }).Committed);
    }

    [Fact]
    public void NativeCallbackReentrancyCannotApplyTheSameApprovalTwice()
    {
        using var world = DamageWorld();
        var climber = world.Nodes[2].Game.BelayImpl;
        climber.PartnerFall = new(Claim(world).Session, 1, 6, 4, 0);
        DamageTicks(world, 1);
        var approval = DamageEvent(world, ManualBelayDamagePhase.ClimberApproval);
        climber.DuringDamage = _ => { world.Nodes[2].Runtime.ApplyEvent(approval); world.Tick(); };
        DamageTicks(world, 4);
        Assert.Single(climber.DamageRequests);
        Assert.Single(world.Nodes[1].Game.BelayImpl.DamageRequests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void IncompleteThrowingOrInvalidReadbackCallsNeverRetryOrShare(int failure)
    {
        using var world = DamageWorld();
        var climber = world.Nodes[2].Game.BelayImpl;
        climber.DamageCompletes = failure != 0; climber.DamageThrows = failure == 1; climber.InvalidDamageReadback = failure == 2;
        var observation = new PartnerFallObservation(Claim(world).Session, 1, 6, 4, .1f);
        climber.PartnerFall = observation;
        DamageTicks(world, 4);
        climber.PartnerFall = observation;
        DamageTicks(world, 4);
        Assert.Single(climber.DamageRequests);
        Assert.Empty(world.Nodes[1].Game.BelayImpl.DamageRequests);
        Assert.DoesNotContain(world.Events, e => e.EventId == "manual-belay.damage-approved"
            && DecodeDamage(e).Phase == ManualBelayDamagePhase.BelayerDose);
        Assert.True(climber.Active); // HP application failure alone does not end belay.
        Assert.Empty(world.Failures);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void OnlyTheCurrentClimberCanClaimOrConfirmDamage(int wrongActor)
    {
        using var world = DamageWorld();
        var claim = Claim(world);
        Assert.False(DamageCommand(world, wrongActor, "claim", claim).Committed);
        Assert.True(DamageCommand(world, 2, "claim", claim).Committed);
        var receipt = new ManualBelayDamageReceipt { Session = claim.Session, Fall = 1, Completed = true, ObservedFraction = .6f };
        Assert.False(DamageCommand(world, wrongActor, "receipt", receipt).Committed);
        Assert.Empty(world.Nodes[1].Game.BelayImpl.DamageRequests);
        Assert.Empty(world.Nodes[2].Game.BelayImpl.DamageRequests);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    [InlineData(10)] [InlineData(11)] [InlineData(12)]
    public void InvalidClaimMeasurementsNeverReachAnOwnerHpCall(int field)
    {
        using var world = DamageWorld();
        var claim = Claim(world);
        switch (field)
        {
            case 0: claim.Session = Guid.Empty; break;
            case 1: claim.Fall = 0; break;
            case 2: claim.Height = float.NaN; break;
            case 3: claim.Height = .249f; break;
            case 4: claim.Height = 83.001f; break;
            case 5: claim.Height = float.PositiveInfinity; break;
            case 6: claim.Length = float.NaN; break;
            case 7: claim.Length = 0; break;
            case 8: claim.Length = -1; break;
            case 9: claim.Length = 43.001f; break;
            case 10: claim.NativeLossFraction = -.001f; break;
            case 11: claim.NativeLossFraction = 1.001f; break;
            default: claim.NativeLossFraction = float.NaN; break;
        }
        Assert.False(DamageCommand(world, 2, "claim", claim).Committed);
        DamageTicks(world);
        Assert.DoesNotContain(world.Events, e => e.EventId == "manual-belay.damage-approved");
        Assert.All(world.Nodes.Values, node => Assert.Empty(node.Game.BelayImpl.DamageRequests));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-.001f)]
    [InlineData(.602f)]
    public void AReceiptCannotExceedOrInvalidateTheApprovedAddition(float observed)
    {
        using var world = DamageWorld();
        var claim = Claim(world);
        Assert.True(DamageCommand(world, 2, "claim", claim).Committed);
        Assert.False(DamageCommand(world, 2, "receipt", new ManualBelayDamageReceipt {
            Session = claim.Session, Fall = 1, Completed = true, ObservedFraction = observed }).Committed);
        DamageTicks(world);
        Assert.All(world.Nodes.Values, node => Assert.Empty(node.Game.BelayImpl.DamageRequests));
    }

    [Fact]
    public void AFailedReceiptDoesNotShareEvenWhenNativeCreditWasPositive()
    {
        using var world = DamageWorld();
        var claim = Claim(world, native: .7f);
        Assert.True(DamageCommand(world, 2, "claim", claim).Committed);
        var failed = new ManualBelayDamageReceipt { Session = claim.Session, Fall = 1, Completed = false, ObservedFraction = 0 };
        Assert.True(DamageCommand(world, 2, "receipt", failed).Committed);
        Assert.False(DamageCommand(world, 2, "receipt", failed).Committed);
        DamageTicks(world);
        Assert.Empty(world.Nodes[1].Game.BelayImpl.DamageRequests);
    }

    [Fact]
    public void PendingDamageBlocksRolePreparationUntilTheReceiptIsConsumed()
    {
        using var world = DamageWorld();
        var claim = Claim(world);
        Assert.True(DamageCommand(world, 2, "claim", claim).Committed);
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.ProposeSwap);
        Assert.DoesNotContain(world.Messages, m => m.Phase == ManualBelayPhase.SwapOffered);
        Assert.True(DamageCommand(world, 2, "receipt", new ManualBelayDamageReceipt {
            Session = claim.Session, Fall = 1, Completed = false }).Committed);
        world.Nodes[2].Game.PanelImpl.Act(BelayPanelAction.ProposeSwap);
        Assert.Contains(world.Messages, m => m.Phase == ManualBelayPhase.SwapOffered);
    }

    [Fact]
    public void LateReceiptsExpireWithoutReopeningTheOldFallSequence()
    {
        using var world = DamageWorld();
        var claim = Claim(world);
        Assert.True(DamageCommand(world, 2, "claim", claim).Committed);
        DamageTicks(world, 21, .5f); // Keep ordinary rope leases fresh while the receipt expires.
        Assert.True(world.Nodes[2].Game.BelayImpl.Active);
        Assert.False(DamageCommand(world, 2, "receipt", new ManualBelayDamageReceipt {
            Session = claim.Session, Fall = 1, Completed = true, ObservedFraction = .6f }).Committed);
        Assert.False(DamageCommand(world, 2, "claim", claim).Committed);
        Assert.True(DamageCommand(world, 2, "claim", Claim(world, 2)).Committed);
        Assert.Empty(world.Nodes[1].Game.BelayImpl.DamageRequests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ContextLossBetweenApprovalAndNativeEntryDiscardsTheQueuedApplication(int pausedPlayer)
    {
        using var world = DamageWorld();
        world.Nodes[2].Game.BelayImpl.PartnerFall = new(Claim(world).Session, 1, 6, 4, 0);
        DamageTicks(world, 1);
        world.Nodes[pausedPlayer].Game.Keys.Paused = true;
        DamageTicks(world, 1);
        world.Nodes[pausedPlayer].Game.Keys.Paused = false;
        DamageTicks(world, 3);
        Assert.All(world.Nodes.Values, node => Assert.Empty(node.Game.BelayImpl.DamageRequests));
    }

    [Fact]
    public void StationLossBetweenApprovalAndNativeEntryCannotCauseDamage()
    {
        using var world = DamageWorld();
        world.Nodes[2].Game.BelayImpl.PartnerFall = new(Claim(world).Session, 1, 6, 4, 0);
        DamageTicks(world, 1);
        world.Nodes[1].Game.BelayImpl.KnownStation = false;
        DamageTicks(world, 3);
        Assert.All(world.Nodes.Values, node => Assert.Empty(node.Game.BelayImpl.DamageRequests));
    }

    [Fact]
    public void OldSessionDamageCannotFollowACommittedRoleExchange()
    {
        using var world = DamageWorld();
        var claim = Claim(world);
        Assert.True(DamageCommand(world, 2, "claim", claim).Committed);
        var approval = DamageEvent(world, ManualBelayDamagePhase.ClimberApproval);
        Assert.True(DamageCommand(world, 2, "receipt", new ManualBelayDamageReceipt {
            Session = claim.Session, Fall = 1, Completed = false }).Committed);
        ProposeSwap(world); world.Nodes[1].Game.PanelImpl.Act(BelayPanelAction.AcceptSwap);
        DamageTicks(world, 2);
        var swapped = world.Last(ManualBelayPhase.SwapCommitted);
        foreach (var node in world.Nodes.Values) node.Runtime.ApplyEvent(approval);
        Assert.False(DamageCommand(world, 2, "claim", claim).Committed);
        DamageTicks(world);
        Assert.All(world.Nodes.Values, node => Assert.Empty(node.Game.BelayImpl.DamageRequests));
        world.Nodes[1].Game.BelayImpl.PartnerFall = new(swapped.Swap, 1, 6, 4, 0);
        DamageTicks(world, 4);
        Assert.False(Assert.Single(world.Nodes[1].Game.BelayImpl.DamageRequests).Belayer);
        Assert.True(Assert.Single(world.Nodes[2].Game.BelayImpl.DamageRequests).Belayer);
    }

    [Fact]
    public void StoppingThePairDropsBothQueuedApplicationsAndOldMessages()
    {
        using var world = DamageWorld();
        world.Nodes[2].Game.BelayImpl.PartnerFall = new(Claim(world).Session, 1, 6, 4, 0);
        DamageTicks(world, 1);
        var approval = DamageEvent(world, ManualBelayDamagePhase.ClimberApproval);
        world.Nodes[1].Game.ChatImpl.Run("stop");
        foreach (var node in world.Nodes.Values) node.Runtime.ApplyEvent(approval);
        DamageTicks(world);
        Assert.All(world.Nodes.Values, node => Assert.Empty(node.Game.BelayImpl.DamageRequests));
    }

    [Fact]
    public void DisabledSharedDamageDoesNotPollOrApproveAFall()
    {
        using var world = World.Started();
        world.Nodes[2].Game.BelayImpl.PartnerFall = new(Claim(world).Session, 1, 6, 4, 0);
        Assert.False(DamageCommand(world, 2, "claim", Claim(world)).Committed);
        DamageTicks(world);
        Assert.All(world.Nodes.Values, node => Assert.Empty(node.Game.BelayImpl.DamageRequests));
    }

    [Fact]
    public void SharedDamagePacketsHaveExactFixedSizesAndRetainTheirRoleAndFallIdentity()
    {
        var session = Guid.NewGuid();
        var claim = new ManualBelayDamageClaim { Session = session, Fall = 9, Height = 6, Length = 4, NativeLossFraction = .1f };
        var claimCopy = DamagePacketRoundTrip(claim, 32);
        Assert.Equal(session, claimCopy.Session); Assert.Equal(9u, claimCopy.Fall);
        Assert.Equal(claim.Height, claimCopy.Height); Assert.Equal(claim.Length, claimCopy.Length);
        Assert.Equal(claim.NativeLossFraction, claimCopy.NativeLossFraction);
        var receipt = new ManualBelayDamageReceipt { Session = session, Fall = 9, Completed = true, ObservedFraction = .5f };
        var receiptCopy = DamagePacketRoundTrip(receipt, 25);
        Assert.Equal(session, receiptCopy.Session); Assert.Equal(9u, receiptCopy.Fall);
        Assert.True(receiptCopy.Completed); Assert.Equal(.5f, receiptCopy.ObservedFraction);
        var message = new ManualBelayDamageMessage { Phase = ManualBelayDamagePhase.BelayerDose,
            Session = session, Fall = 9, Climber = 2, Belayer = 1, Fraction = .18f };
        var messageCopy = DamagePacketRoundTrip(message, 33);
        Assert.Equal(session, messageCopy.Session); Assert.Equal(9u, messageCopy.Fall);
        Assert.Equal(2, messageCopy.Climber); Assert.Equal(1, messageCopy.Belayer);
        Assert.Equal(message.Phase, messageCopy.Phase); Assert.Equal(message.Fraction, messageCopy.Fraction);
        var badBoolean = Bytes(receipt); badBoolean[^1] = 2;
        using var reader = new BinaryReader(new MemoryStream(badBoolean));
        Assert.Throws<InvalidDataException>(() => new ManualBelayDamageReceipt().Deserialize(reader));
    }

    private static T DamagePacketRoundTrip<T>(T packet, int size) where T : IPacket, new()
    {
        var bytes = Bytes(packet); Assert.Equal(size, bytes.Length);
        var copy = new T();
        using var reader = new BinaryReader(new MemoryStream(bytes)); copy.Deserialize(reader);
        foreach (var delta in new[] { -1, 1 })
        {
            var malformed = (byte[])bytes.Clone(); Array.Resize(ref malformed, size + delta);
            using var invalid = new BinaryReader(new MemoryStream(malformed));
            Assert.Throws<InvalidDataException>(() => new T().Deserialize(invalid));
        }
        return copy;
    }
}
