#nullable enable

using System.Numerics;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerShared.Tests;

public class NativePitonSealTests
{
    private static readonly IntPtr Lifeline = new(1000);
    private static readonly Vector3 Here = new(4f, 2f, 1f);
    private static readonly Vector3 There = new(12f, 3f, 1f);

    [Fact]
    public void APitonAlreadyInTheRockWhenTheTieStartsIsNotAnnounced()
    {
        var sync = Settling();
        sync.SealNative(new IntPtr(1), Here);

        Assert.Empty(sync.Reconcile(Placed((1, Here)), null, 0).Announce);
        Assert.Empty(sync.Reconcile(Placed((1, Here)), null, PitonSync.SettleSeconds + 1).Announce);
    }

    [Fact]
    public void AReplacementAtTheSamePlaceStaysOffTheChain()
    {
        var sync = Settling();
        sync.SealNative(new IntPtr(1), Here);
        sync.Reconcile(Placed((1, Here)), null, 0);

        var actions = sync.Reconcile(Placed((9, Here)), null, PitonSync.SettleSeconds + 1);

        Assert.Empty(actions.Announce);
        Assert.True(sync.IsSealedNative(new IntPtr(9), Here));
    }

    [Fact]
    public void APitonPlacedAfterTheTieIsAnnounced()
    {
        var sync = Settling();
        sync.SealNative(new IntPtr(1), Here);
        sync.BeginTie();
        sync.Reconcile(Placed((1, Here)), null, 0);

        var actions = sync.Reconcile(Placed((1, Here), (2, There)), null, PitonSync.SettleSeconds + 1);

        var announced = Assert.Single(actions.Announce);
        Assert.Equal(new IntPtr(2), announced.Pointer);
    }

    [Fact]
    public void RecallingTheNativePitonLetsALaterOneAtThatPlaceJoin()
    {
        var sync = Settling();
        sync.SealNative(new IntPtr(1), Here);
        sync.Reconcile(Placed((1, Here)), null, 0);
        sync.Reconcile(Placed(), null, PitonSync.SettleSeconds + 1);

        var actions = sync.Reconcile(Placed((2, Here)), null, PitonSync.SettleSeconds + 2);

        Assert.Equal(new IntPtr(2), Assert.Single(actions.Announce).Pointer);
        Assert.False(sync.IsSealedNative(new IntPtr(2), Here));
    }

    [Fact]
    public void APitonKnownBeforeTheTieIsNotTreatedAsPlacedOnIt()
    {
        var sync = new PitonSync();
        sync.Know(Remote(7, Here));
        sync.BeginTie();

        Assert.True(sync.KnownFromBeforeThisTie(7));
        sync.Know(Remote(8, There));
        Assert.False(sync.KnownFromBeforeThisTie(8));
    }

    private static PitonSync Settling()
    {
        var sync = new PitonSync();
        sync.OnLifeline(Lifeline, 0);
        return sync;
    }

    private static System.Collections.Generic.List<PlacedPiton> Placed(params (int Pointer, Vector3 Position)[] pitons)
    {
        var placed = new System.Collections.Generic.List<PlacedPiton>();
        foreach (var (pointer, position) in pitons)
            placed.Add(new PlacedPiton(new IntPtr(pointer), position));
        return placed;
    }

    private static ServerPitonPlaced Remote(uint id, Vector3 at)
        => new()
        {
            FromPlayerId = 2,
            PitonId = id,
            SourcePitonId = id,
            PosX = at.X,
            PosY = at.Y,
            PosZ = at.Z,
            RotW = 1,
            Quality = 1,
            PitonHp = 100,
            ItemId = 5,
        };
}
