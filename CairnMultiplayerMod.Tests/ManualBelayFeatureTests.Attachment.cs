using System.Linq;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.GameApi;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed partial class ManualBelayFeatureTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void AnExistingStationRouteCanBeAcceptedWithoutAClimberStationOrPersonalAnchor(int belayer, int climber)
    {
        using var world = new World();
        var target = world.Nodes[climber].Game;
        target.BelayImpl.Anchored = false;
        target.BelayImpl.NeedsAttachment = false;
        world.Nodes[belayer].Game.ChatImpl.Run("offer " + climber);
        var offer = world.Last(ManualBelayPhase.Offered);
        Assert.False(target.BelayImpl.HasStation);
        Assert.True(target.PanelImpl.View.CanAccept);
        target.PanelImpl.Act(BelayPanelAction.Accept);
        world.Tick();
        Assert.True(target.BelayImpl.Active);
        Assert.Equal(1, target.BelayImpl.BeginCalls);
        Assert.Null(target.BelayImpl.Preparation);
        Assert.Equal(offer.Station, target.BelayImpl.BoundStation);
        Assert.False(target.BelayImpl.Anchored);
        Assert.False(target.BelayImpl.HasStation);
        Assert.Equal(offer.Session, world.Nodes[belayer].Game.BelayImpl.ProtectedSession);
        Assert.Empty(world.Failures);
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(0, 2, false)]
    [InlineData(1, 1, false)]
    [InlineData(1, 2, false)]
    [InlineData(2, 1, false)]
    [InlineData(2, 2, false)]
    [InlineData(0, 1, true)]
    [InlineData(0, 2, true)]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, true)]
    [InlineData(2, 1, true)]
    [InlineData(2, 2, true)]
    public void AnInterruptionStopsActiveAndPreparingPairsWithoutAutomaticResume(int interruption, int participant, bool preparing)
    {
        using var world = new World();
        world.Nodes[2].Game.BelayImpl.NeedsAttachment = preparing;
        world.Nodes[1].Game.ChatImpl.Run("offer 2"); world.Nodes[2].Game.ChatImpl.Run("accept"); world.Tick();
        var interrupted = world.Nodes[participant].Game;
        if (interruption == 0) interrupted.Keys.Paused = true;
        if (interruption == 1) interrupted.Cutscene = true;
        if (interruption == 2) interrupted.Keys.Focused = false;
        world.Tick();
        Assert.Equal(ManualBelayPhase.Ended, world.Messages.Last().Phase);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        Assert.Null(world.Nodes[2].Game.BelayImpl.Preparation);
        var begins = world.Nodes[2].Game.BelayImpl.BeginCalls;
        interrupted.Keys.Paused = false; interrupted.Cutscene = false; interrupted.Keys.Focused = true;
        for (var i = 0; i < 5; i++) Advance(world, .5f);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        Assert.Equal(begins, world.Nodes[2].Game.BelayImpl.BeginCalls);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void InitialAttachmentWaitsForNativeConfirmationWhileFreshPartnerIntentKeepsConsent(int belayer, int climber)
    {
        using var world = new World();
        var target = world.Nodes[climber].Game.BelayImpl;
        target.NeedsAttachment = true;
        world.Nodes[belayer].Game.ChatImpl.Run("offer " + climber);
        world.Nodes[climber].Game.ChatImpl.Run("accept");
        world.Tick();
        Assert.True(target.Status.Rebinding);
        Assert.Null(target.Rope); Assert.Equal(0, target.BeginCalls);
        world.Nodes[belayer].Game.Keys.Up = true;
        for (var i = 0; i < 5; i++) Advance(world, .11f);
        Assert.Null(target.Rope);
        Assert.True(world.Last(ManualBelayPhase.Telemetry).Rebinding);
        Assert.False(world.Last(ManualBelayPhase.Telemetry).LengthApplied);
        world.Nodes[belayer].Game.Keys.Pressed = true;
        Advance(world, .11f);
        target.NativeAttached = true;
        for (var i = 0; i < 40; i++) Advance(world, .11f);
        Assert.Null(target.Rope); Assert.Equal(0, target.BeginCalls);
        for (var i = 0; i < 15; i++) Advance(world, .11f);
        Assert.True(target.Active); Assert.Equal(1, target.BeginCalls);
        Assert.True(target.LastBrake); Assert.True(target.Rope.Braked);
        Assert.Equal(8, target.Rope.Released); Assert.Equal(0, target.Rope.Motion);
        Assert.Empty(world.Failures);
    }

    [Fact]
    public void LossOfFreshInputDuringAttachmentCannotAcquireRopeControl()
    {
        using var world = new World();
        var target = world.Nodes[2].Game.BelayImpl;
        target.NeedsAttachment = true; target.NativeAttached = true;
        world.Nodes[1].Game.ChatImpl.Run("offer 2"); world.Nodes[2].Game.ChatImpl.Run("accept"); world.Tick();
        world.DroppedEventRecipient = 2;
        for (var i = 0; i < 40; i++) Advance(world, .11f);
        Assert.False(target.Active); Assert.Null(target.Preparation);
        Assert.Null(target.Rope); Assert.Equal(0, target.BeginCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithdrawnHostRegistryStationEndsBothInvitationAndActivePreparation(bool accepted)
    {
        using var world = new World();
        world.Nodes[2].Game.BelayImpl.NeedsAttachment = true;
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        if (accepted) world.Nodes[2].Game.ChatImpl.Run("accept");
        world.Nodes[1].Game.BelayImpl.KnownStation = false;
        world.Tick();
        Assert.Equal(ManualBelayPhase.Ended, world.Messages.Last().Phase);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.BeginCalls);
    }

    [Fact]
    public void AStationAbsentFromTheHostRegistryCannotBeOffered()
    {
        using var world = new World(); world.Nodes[1].Game.BelayImpl.KnownStation = false;
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        Assert.Empty(world.Messages);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AcceptanceCannotSubstituteAnotherPitonAtTheSamePosition(bool owner)
    {
        using var world = new World(); world.Nodes[1].Game.ChatImpl.Run("offer 2");
        var offer = world.Last(ManualBelayPhase.Offered);
        var changed = owner ? offer.Station with { PitonOwner = 3 } : offer.Station with { PitonId = 8 };
        Assert.False(world.Execute(2, new ManualBelayCommand { Operation = ManualBelayOperation.Accept,
            Session = offer.Session, Station = changed }).Committed);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.BeginCalls);
    }
}
