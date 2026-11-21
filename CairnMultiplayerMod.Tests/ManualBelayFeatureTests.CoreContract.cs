using System;
using System.IO;
using System.Linq;
using CairnMultiplayerMod.Features;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed partial class ManualBelayFeatureTests
{
    [Fact]
    public void AnAcceptedInvitationThatExpiresLocallyCannotBeReplayedIntoNativeControl()
    {
        using var world = new World(participants: 2);
        world.Nodes[1].Game.ChatImpl.Run("offer 2");
        var offered = world.Events.Last(e => Decode(e).Phase == ManualBelayPhase.Offered);
        world.DroppedEventRecipient = 2;
        world.Nodes[2].Game.ChatImpl.Run("accept");
        var started = world.Events.Last(e => Decode(e).Phase == ManualBelayPhase.Started);
        for (var i = 0; i < 42; i++) Advance(world, .5f);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.BeginCalls);
        world.DroppedEventRecipient = -1;
        world.Nodes[2].Runtime.ApplyEvent(offered);
        world.Nodes[2].Runtime.ApplyEvent(started);
        Advance(world, .11f);
        Assert.Equal(0, world.Nodes[2].Game.BelayImpl.BeginCalls);
        Assert.False(world.Nodes[2].Game.BelayImpl.Active);
        Assert.False(world.Nodes[2].Game.PanelImpl.IsOpen);
    }

    [Fact]
    public void CorePacketsHaveNoReservedRoleOrRecoveryTail()
    {
        var request = Bytes(new ManualBelayCommand { Operation = ManualBelayOperation.Intent, Session = Guid.NewGuid() });
        var changed = Bytes(new ManualBelayMessage { Phase = ManualBelayPhase.Telemetry, Session = Guid.NewGuid() });
        Assert.Equal(102, request.Length); Assert.Equal(110, changed.Length);
        var extendedRequest = new byte[request.Length + 32]; request.CopyTo(extendedRequest, 0);
        var extendedChanged = new byte[changed.Length + 32]; changed.CopyTo(extendedChanged, 0);
        using var requestReader = new BinaryReader(new MemoryStream(extendedRequest));
        using var eventReader = new BinaryReader(new MemoryStream(extendedChanged));
        Assert.Throws<InvalidDataException>(() => new ManualBelayCommand().Deserialize(requestReader));
        Assert.Throws<InvalidDataException>(() => new ManualBelayMessage().Deserialize(eventReader));
    }

    [Theory]
    [InlineData(true, 6)]
    [InlineData(true, 255)]
    [InlineData(false, 5)]
    [InlineData(false, 255)]
    public void CoreDecodersRejectOperationsAndPhasesOutsideTheirOwnContract(bool request, byte value)
    {
        var bytes = request ? Bytes(new ManualBelayCommand()) : Bytes(new ManualBelayMessage());
        bytes[0] = value;
        using var reader = new BinaryReader(new MemoryStream(bytes));
        if (request) Assert.Throws<InvalidDataException>(() => new ManualBelayCommand().Deserialize(reader));
        else Assert.Throws<InvalidDataException>(() => new ManualBelayMessage().Deserialize(reader));
    }
}
