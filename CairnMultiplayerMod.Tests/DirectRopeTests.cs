using System;
using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class DirectRopeTests
{
    [Fact]
    public void PlayerRopeUsesDedicatedKeyInsteadOfNativeInteractionKey()
    {
        Assert.Equal("L", RopeCoupleController.ToggleKeyName);
        Assert.NotEqual("E", RopeCoupleController.ToggleKeyName);
    }

    private sealed class Binding : IRopeBinding
    {
        public bool IsReady { get; set; }
        internal bool Accepted = true, Alive = true, ThrowOnAttach;
        internal int Attachments, Disposals;
        public bool Attach()
        {
            Attachments++;
            if (ThrowOnAttach) throw new InvalidOperationException("native refusal");
            return Accepted;
        }
        public bool Maintain() => Alive;
        public void Dispose() => Disposals++;
    }

    [Fact]
    public void WaitsForNativeActorThenAttachesOnlyOnceAndCleansOnPartnerLoss()
    {
        var native = new Binding();
        using var session = new RopeBindingSession(native, 0);
        Assert.True(session.Tick(0));
        Assert.Equal(0, native.Attachments);
        native.IsReady = true;
        Assert.True(session.Tick(.02));
        Assert.True(session.Tick(.04));
        Assert.Equal(RopeBindingState.Attached, session.State);
        Assert.Equal(1, native.Attachments);
        native.Alive = false;
        Assert.False(session.Tick(.06));
        session.Dispose();
        Assert.Equal(1, native.Disposals);
        Assert.Equal(RopeBindingState.Released, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeRefusalAndExceptionReleasePartialResources(bool throws)
    {
        var native = new Binding { IsReady = true, Accepted = false, ThrowOnAttach = throws };
        using var session = new RopeBindingSession(native, 0);
        if (throws) Assert.Throws<InvalidOperationException>(() => session.Tick(0));
        else Assert.False(session.Tick(0));
        Assert.Equal(1, native.Disposals);
        Assert.False(session.Tick(1));
        Assert.Equal(1, native.Attachments);
    }

    [Fact]
    public void InitializationHasABoundedDeadline()
    {
        var native = new Binding();
        using var session = new RopeBindingSession(native, 2);
        Assert.True(session.Tick(6.9));
        Assert.False(session.Tick(7));
        Assert.Equal(0, native.Attachments);
        Assert.Equal(1, native.Disposals);
    }
}
