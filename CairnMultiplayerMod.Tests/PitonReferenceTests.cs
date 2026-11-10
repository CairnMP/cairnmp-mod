using System;
using System.Numerics;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class PitonReferenceTests
{
    [Fact]
    public void AnAnnouncedOwnPitonResolvesByOwnerAndSourceId()
    {
        var sync = new PitonSync(); sync.OnLifeline((IntPtr)100, 0);
        var actions = sync.Reconcile(new[] { Placed(10) }, Vector3.Zero, 4);
        var id = Assert.Single(actions.Announce).LocalId;
        Assert.True(sync.TryGetReference((IntPtr)10, 1, 4.1, out var owner, out var source));
        Assert.Equal(1, owner); Assert.Equal(id, source);
        Assert.True(sync.TryResolveReference(owner, source, 1, 4.1, out var pointer));
        Assert.Equal((IntPtr)10, pointer);
        Assert.False(sync.TryResolveReference(2, source, 1, 4.1, out _));
    }

    [Fact]
    public void CoincidentRemotePitonsWithTheSameSourceIdHaveDifferentOwners()
    {
        var sync = new PitonSync(); sync.OnLifeline((IntPtr)100, 0);
        sync.Know(Remote(17, 2)); sync.Know(Remote(18, 3));
        sync.Reconcile(Array.Empty<PlacedPiton>(), Vector3.Zero, 4);
        sync.Spawned(17, (IntPtr)20); sync.Spawned(18, (IntPtr)30);
        sync.Reconcile(new[] { Placed(20), Placed(30) }, Vector3.Zero, 4.1);
        Assert.True(sync.TryResolveReference(2, 7, 1, 4.2, out var first));
        Assert.True(sync.TryResolveReference(3, 7, 1, 4.2, out var second));
        Assert.Equal((IntPtr)20, first); Assert.Equal((IntPtr)30, second);
        Assert.False(sync.TryResolveReference(2, 8, 1, 4.2, out _));
    }

    [Fact]
    public void AmbiguousOwnerSourceMappingsCannotSelectEitherNativePointer()
    {
        var sync = RemoteLive();
        sync.Know(Remote(18, 2)); sync.Reconcile(new[] { Placed(20) }, Vector3.Zero, 4.2);
        sync.Spawned(18, (IntPtr)30);
        sync.Reconcile(new[] { Placed(20), Placed(30) }, Vector3.Zero, 4.3);
        Assert.False(sync.TryResolveReference(2, 7, 1, 4.4, out _));
        Assert.False(sync.TryGetReference((IntPtr)20, 1, 4.4, out _, out _));
    }

    [Fact]
    public void WithdrawalImmediatelyInvalidatesAStationBeforeNativeRemoval()
    {
        var sync = RemoteLive(); sync.Forget(17);
        Assert.False(sync.TryResolveReference(2, 7, 1, 4.2, out _));
        Assert.False(sync.TryGetReference((IntPtr)20, 1, 4.2, out _, out _));
    }

    [Fact]
    public void StaleFramesAndSessionResetCannotReturnNativePointers()
    {
        var sync = RemoteLive();
        Assert.False(sync.TryResolveReference(2, 7, 1, 6, out _));
        sync.ForgetSession();
        Assert.False(sync.TryResolveReference(2, 7, 1, 4.2, out _));
    }

    [Fact]
    public void APositionOnlyReloadMatchIsNotAnExactStationReference()
    {
        var sync = RemoteLive(); sync.OnLifeline((IntPtr)101, 5);
        sync.Reconcile(new[] { Placed(30) }, Vector3.Zero, 9);
        Assert.False(sync.TryResolveReference(2, 7, 1, 9.1, out _));
        Assert.False(sync.TryGetReference((IntPtr)30, 1, 9.1, out _, out _));
    }

    [Fact]
    public void AnOwnPitonReboundByPositionCannotMasqueradeAsTheOldStation()
    {
        var sync = new PitonSync(); sync.OnLifeline((IntPtr)100, 0);
        var id = Assert.Single(sync.Reconcile(new[] { Placed(10) }, Vector3.Zero, 4).Announce).LocalId;
        sync.OnLifeline((IntPtr)101, 5);
        sync.Reconcile(new[] { Placed(30) }, Vector3.Zero, 9);
        Assert.False(sync.TryResolveReference(1, id, 1, 9.1, out _));
    }

    [Fact]
    public void TakingUpAnOwnPitonInvalidatesItsStationWithoutReusingItsId()
    {
        var sync = new PitonSync(); sync.OnLifeline((IntPtr)100, 0);
        var first = Assert.Single(sync.Reconcile(new[] { Placed(10) }, Vector3.Zero, 4).Announce).LocalId;
        sync.TakenByOther(first);
        Assert.False(sync.TryResolveReference(1, first, 1, 4.1, out _));
        sync.Reconcile(Array.Empty<PlacedPiton>(), Vector3.Zero, 4.2);
        var second = Assert.Single(sync.Reconcile(new[] { Placed(30) }, Vector3.Zero, 4.3).Announce).LocalId;
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ExactReferencesFollowExistingUnclipAndReclipBookkeepingWithoutChangingItsId()
    {
        var sync = RemoteLive();
        Assert.True(sync.KeepUnclipped((IntPtr)20));
        Assert.True(sync.Reconcile(Array.Empty<PlacedPiton>(), Vector3.Zero, 4.2).IsEmpty);
        Assert.False(sync.TryResolveReference(2, 7, 1, 4.2, out _));
        Assert.True(sync.TryGetId((IntPtr)20, out var authoritativeId, out var own));
        Assert.False(own); Assert.Equal(17u, authoritativeId);
        sync.Reconcile(new[] { Placed(20) }, Vector3.Zero, 4.3);
        Assert.True(sync.TryResolveReference(2, 7, 1, 4.4, out var pointer));
        Assert.Equal((IntPtr)20, pointer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnclippingAndReclippingCannotCertifyAPositionOnlyReloadMatch(bool own)
    {
        var sync = own ? new PitonSync() : RemoteLive();
        if (own)
        {
            sync.OnLifeline((IntPtr)100, 0);
            sync.Reconcile(new[] { Placed(10) }, Vector3.Zero, 4);
        }
        sync.OnLifeline((IntPtr)101, 5);
        sync.Reconcile(new[] { Placed(30) }, Vector3.Zero, 9);
        Assert.True(sync.KeepUnclipped((IntPtr)30));
        sync.Reconcile(Array.Empty<PlacedPiton>(), Vector3.Zero, 9.1);
        sync.Reconcile(new[] { Placed(30) }, Vector3.Zero, 9.2);
        Assert.False(sync.TryGetReference((IntPtr)30, 1, 9.3, out _, out _));
    }

    [Fact]
    public void ForgettingReplicasDoesNotCertifyAnOwnPositionOnlyReloadMatch()
    {
        var sync = new PitonSync(); sync.OnLifeline((IntPtr)100, 0);
        sync.Reconcile(new[] { Placed(10) }, Vector3.Zero, 4);
        sync.OnLifeline((IntPtr)101, 5);
        sync.Reconcile(new[] { Placed(30) }, Vector3.Zero, 9);
        sync.ForgetReplicas();
        Assert.False(sync.TryGetReference((IntPtr)30, 1, 9.1, out _, out _));
    }

    [Fact]
    public void ARepeatedAuthoritativeIdCannotChangeItsOwnerOrSourceIdentity()
    {
        var sync = RemoteLive();
        var changed = Remote(17, 3); sync.Know(changed);
        changed = Remote(17, 2); changed.SourcePitonId = 8; sync.Know(changed);
        Assert.True(sync.TryResolveReference(2, 7, 1, 4.2, out var pointer));
        Assert.Equal((IntPtr)20, pointer);
        Assert.False(sync.TryResolveReference(3, 7, 1, 4.2, out _));
        Assert.False(sync.TryResolveReference(2, 8, 1, 4.2, out _));
    }

    [Theory]
    [InlineData(0u, 7u, 2)]
    [InlineData(17u, 0u, 2)]
    [InlineData(17u, 7u, -1)]
    public void InvalidSnapshotIdentitiesCannotEnterReplicaBookkeeping(uint authoritativeId, uint sourceId, int owner)
    {
        var sync = new PitonSync(); sync.OnLifeline((IntPtr)100, 0);
        var invalid = Remote(authoritativeId, owner); invalid.SourcePitonId = sourceId;
        sync.Know(invalid);
        Assert.Equal(0, sync.RemoteCount);
        Assert.Empty(sync.Reconcile(Array.Empty<PlacedPiton>(), Vector3.Zero, 4).Spawn);
    }

    private static PitonSync RemoteLive()
    {
        var sync = new PitonSync(); sync.OnLifeline((IntPtr)100, 0); sync.Know(Remote(17, 2));
        sync.Reconcile(Array.Empty<PlacedPiton>(), Vector3.Zero, 4); sync.Spawned(17, (IntPtr)20);
        sync.Reconcile(new[] { Placed(20) }, Vector3.Zero, 4.1);
        return sync;
    }

    private static PlacedPiton Placed(int pointer) => new((IntPtr)pointer, Vector3.Zero);
    private static ServerPitonPlaced Remote(uint id, int owner) => new()
    { PitonId = id, SourcePitonId = 7, FromPlayerId = owner, RotW = 1, Quality = 1, PitonHp = 100, ItemId = 5 };
}
