using System;
using System.Collections.Generic;
using System.Numerics;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class PitonSyncTests
{
    private static readonly IntPtr Lifeline = new(1000);
    private static readonly IntPtr ReloadedLifeline = new(2000);
    private static readonly Vector3 A = new(1, 0, 0), B = new(2, 0, 0), C = new(10, 0, 0);
    private const double Settled = PitonSync.SettleSeconds + 0.1;

    [Fact]
    public void OurNewPitonsAreAnnouncedOnceTheLifelineSettles()
    {
        var sync = Started(out var t);
        var actions = sync.Reconcile(Placed((1, A), (2, B)), null, t);
        Assert.Empty(actions.Announce);

        actions = sync.Reconcile(Placed((1, A), (2, B)), null, t += Settled);
        Assert.Equal(2, actions.Announce.Count);
        Assert.NotEqual(actions.Announce[0].LocalId, actions.Announce[1].LocalId);

        Assert.True(sync.Reconcile(Placed((1, A), (2, B)), null, t + 0.1).IsEmpty);
    }

    [Fact]
    public void APitonOfOursThatLeavesTheLifelineIsWithdrawn()
    {
        var sync = Started(out var t);
        var announced = sync.Reconcile(Placed((1, A), (2, B)), null, t += Settled).Announce;
        var first = announced.Find(entry => entry.Pointer == new IntPtr(1)).LocalId;

        var actions = sync.Reconcile(Placed((2, B)), null, t + 0.1);

        Assert.Equal(new[] { first }, actions.Removed);
    }

    [Fact]
    public void AReplacementInTheSameFrameIsBothWithdrawnAndAnnounced()
    {
        var sync = Started(out var t);
        var old = sync.Reconcile(Placed((1, A)), null, t += Settled).Announce[0].LocalId;

        var actions = sync.Reconcile(Placed((2, B)), null, t + 0.1);

        Assert.Equal(new[] { old }, actions.Removed);
        Assert.NotEqual(old, Assert.Single(actions.Announce).LocalId);
    }

    [Fact]
    public void SomeoneElsesPitonIsCreatedOnceAndNeverAnnounced()
    {
        var sync = Started(out var t);
        sync.Know(Piton(7, C));

        var actions = sync.Reconcile(Placed(), null, t += Settled);
        Assert.Equal(7u, Assert.Single(actions.Spawn).PitonId);
        sync.Spawned(7, new IntPtr(70));

        actions = sync.Reconcile(Placed((70, C)), null, t + 0.1);
        Assert.True(actions.IsEmpty);
    }

    [Fact]
    public void ACreatedPitonWhosePointerWasMissedIsKnownByItsPlace()
    {
        var sync = Started(out var t);
        sync.Know(Piton(7, C));
        sync.Reconcile(Placed(), null, t += Settled);
        sync.Spawned(7, IntPtr.Zero);

        var actions = sync.Reconcile(Placed((70, C)), null, t + 0.1);

        Assert.Empty(actions.Announce);
        Assert.Empty(actions.Spawn);
    }

    [Fact]
    public void TakingUpSomeoneElsesPitonTellsTheHost()
    {
        var sync = WithLiveRemote(out var t);

        var actions = sync.Reconcile(Placed(), C + new Vector3(0, 1, 0), t + 0.1);

        Assert.Equal(new[] { 7u }, actions.Recovered);
        Assert.Equal(0, sync.RemoteCount);
    }

    [Fact]
    public void AMarkedTakeIsReportedEvenWhenThePawnIsFarFromTheAnnouncedRoot()
    {
        var sync = WithLiveRemote(out var t);
        sync.MarkTaken(new IntPtr(70));

        var actions = sync.Reconcile(Placed(), C + new Vector3(50, 0, 0), t + 0.1);

        Assert.Equal(new[] { 7u }, actions.Recovered);
        Assert.Equal(0, sync.RemoteCount);
    }

    [Fact]
    public void AnUnclippedReplicaStaysWhenItLeavesThePlacedList()
    {
        var sync = WithLiveRemote(out var t);
        sync.KeepUnclipped(new IntPtr(70));

        // Standing on it, or far below it, must not be reported as a take.
        var near = sync.Reconcile(Placed(), C, t + 0.1);
        Assert.Empty(near.Recovered);
        Assert.True(sync.TryGetRemotePointer(7, out var pointer));
        Assert.Equal(new IntPtr(70), pointer);

        var far = sync.Reconcile(Placed(), C + new Vector3(50, 0, 0), t + 0.2);
        Assert.Empty(far.Recovered);
        Assert.True(sync.TryGetRemotePointer(7, out pointer));
        Assert.Equal(new IntPtr(70), pointer);

        var held = new List<IntPtr>();
        sync.CopyUnclipped(held);
        Assert.Equal(new[] { new IntPtr(70) }, held);
    }

    [Fact]
    public void TakingAnUnclippedReplicaStillTellsTheHost()
    {
        var sync = WithLiveRemote(out var t);
        sync.KeepUnclipped(new IntPtr(70));
        sync.MarkTaken(new IntPtr(70));

        var actions = sync.Reconcile(Placed(), C + new Vector3(50, 0, 0), t + 0.1);

        Assert.Equal(new[] { 7u }, actions.Recovered);
        Assert.False(sync.TryGetRemotePointer(7, out _));
        var held = new List<IntPtr>();
        sync.CopyUnclipped(held);
        Assert.Empty(held);
    }

    [Fact]
    public void AnUnclippedPitonOfOursStaysWhenItLeavesThePlacedList()
    {
        var sync = Started(out var t);
        var announced = sync.Reconcile(Placed((1, A)), null, t += Settled).Announce[0];
        sync.KeepUnclipped(announced.Pointer);

        var actions = sync.Reconcile(Placed(), null, t + 0.1);

        Assert.Empty(actions.Removed);
        Assert.Empty(actions.Strip);
        Assert.True(sync.TryGetOwnPointer(announced.LocalId, out var pointer));
        Assert.Equal(announced.Pointer, pointer);
        var held = new List<IntPtr>();
        sync.CopyUnclipped(held);
        Assert.Equal(new[] { announced.Pointer }, held);
    }

    [Fact]
    public void TakingAnUnclippedPitonOfOursWithdrawsItOnce()
    {
        var sync = Started(out var t);
        var announced = sync.Reconcile(Placed((1, A)), null, t += Settled).Announce[0];
        sync.KeepUnclipped(announced.Pointer);
        Assert.Empty(sync.Reconcile(Placed(), null, t += 0.1).Removed);

        sync.MarkTaken(announced.Pointer);
        var actions = sync.Reconcile(Placed(), null, t + 0.1);

        Assert.Equal(new[] { announced.LocalId }, actions.Removed);
        Assert.Empty(actions.Strip);
        var held = new List<IntPtr>();
        sync.CopyUnclipped(held);
        Assert.Empty(held);
        Assert.True(sync.Reconcile(Placed(), null, t + 0.2).IsEmpty);
    }

    [Fact]
    public void AnotherClimberTakingOurUnclippedPitonDeletesItOnce()
    {
        var sync = Started(out var t);
        var announced = sync.Reconcile(Placed((1, A)), null, t += Settled).Announce[0];
        sync.KeepUnclipped(announced.Pointer);
        sync.Reconcile(Placed(), null, t += 0.1);

        sync.TakenByOther(announced.LocalId);
        var actions = sync.Reconcile(Placed(), null, t + 0.1);

        Assert.Equal(new[] { announced.Pointer }, actions.Strip);
        Assert.Empty(actions.Removed);
        Assert.False(sync.TryGetOwnPointer(announced.LocalId, out _));
        var held = new List<IntPtr>();
        sync.CopyUnclipped(held);
        Assert.Empty(held);
        Assert.True(sync.Reconcile(Placed(), null, t + 0.2).IsEmpty);
    }

    [Fact]
    public void AWithdrawnUnclippedReplicaIsDeletedOnce()
    {
        var sync = WithLiveRemote(out var t);
        sync.KeepUnclipped(new IntPtr(70));
        Assert.Empty(sync.Reconcile(Placed(), C + new Vector3(50, 0, 0), t += 0.1).Recovered);

        sync.Forget(7);
        var actions = sync.Reconcile(Placed(), C + new Vector3(50, 0, 0), t + 0.1);

        Assert.Empty(actions.Recovered);
        Assert.Equal(new[] { new IntPtr(70) }, actions.Strip);
        Assert.False(sync.TryGetRemotePointer(7, out _));
        var held = new List<IntPtr>();
        sync.CopyUnclipped(held);
        Assert.Empty(held);
        Assert.True(sync.Reconcile(Placed(), null, t + 0.2).IsEmpty);
    }

    [Fact]
    public void AnUnclippedPitonOfOursThatReturnsToTheListCanLeaveForGood()
    {
        var sync = Started(out var t);
        var announced = sync.Reconcile(Placed((1, A)), null, t += Settled).Announce[0];
        sync.KeepUnclipped(announced.Pointer);
        Assert.Empty(sync.Reconcile(Placed(), null, t += 0.1).Removed);

        Assert.True(sync.Reconcile(Placed((1, A)), null, t += 0.1).IsEmpty);
        var actions = sync.Reconcile(Placed(), null, t + 0.1);

        Assert.Equal(new[] { announced.LocalId }, actions.Removed);
        var held = new List<IntPtr>();
        sync.CopyUnclipped(held);
        Assert.Empty(held);
    }

    [Fact]
    public void AnUnclippedReplicaThatReturnsToTheListCanLeaveForGood()
    {
        var sync = WithLiveRemote(out var t);
        sync.KeepUnclipped(new IntPtr(70));
        // Near the piton. Without the keep this would be reported as a take.
        Assert.Empty(sync.Reconcile(Placed(), C, t += 0.1).Recovered);

        // Back on the list: the keep ends. A later disappearance far away is abandoned.
        Assert.True(sync.Reconcile(Placed((70, C)), C + new Vector3(50, 0, 0), t += 0.1).IsEmpty);
        var actions = sync.Reconcile(Placed(), C + new Vector3(50, 0, 0), t + 0.1);

        Assert.Empty(actions.Recovered);
        Assert.False(sync.TryGetRemotePointer(7, out _));
    }

    [Fact]
    public void APitonVanishingFarFromUsIsNotOursToAnnounceNorToTouchAgain()
    {
        var sync = WithLiveRemote(out var t);

        var actions = sync.Reconcile(Placed(), C + new Vector3(50, 0, 0), t += 0.1);
        Assert.Empty(actions.Recovered);
        Assert.Empty(actions.Strip);

        // Its owner withdraws it later: the stale pointer is never handed back.
        sync.Forget(7);
        actions = sync.Reconcile(Placed(), null, t + 0.1);
        Assert.Empty(actions.Detach);
        Assert.Empty(actions.Strip);
        Assert.Empty(actions.Spawn);
    }

    [Fact]
    public void ADisappearanceAcrossALoadingScreenIsNotPutDownToUs()
    {
        var sync = WithLiveRemote(out var t);

        var actions = sync.Reconcile(Placed(), C, t + 30);

        Assert.Empty(actions.Recovered);
    }

    [Fact]
    public void AWithdrawnPitonIsTakenOffOnlyWhileTheLifelineHoldsIt()
    {
        var sync = WithLiveRemote(out var t);
        sync.Forget(7);

        var actions = sync.Reconcile(Placed((70, C)), null, t += 0.1);
        Assert.Equal(new[] { new IntPtr(70) }, actions.Strip);
        Assert.Empty(actions.Detach);

        actions = sync.Reconcile(Placed(), null, t + 0.1);
        Assert.True(actions.IsEmpty);
        Assert.Equal(0, sync.RemoteCount);
    }

    [Fact]
    public void AfterAReloadOurSavedPitonsKeepTheirIds()
    {
        var sync = Started(out var t);
        var id = sync.Reconcile(Placed((1, A)), null, t += Settled).Announce[0].LocalId;

        sync.OnLifeline(ReloadedLifeline, t += 1);
        Assert.True(sync.Reconcile(Placed((11, A)), null, t).IsEmpty);
        Assert.True(sync.Reconcile(Placed((11, A)), null, t += Settled).IsEmpty);

        // Taken up later, it is withdrawn under the id the others know it by.
        Assert.Equal(new[] { id }, sync.Reconcile(Placed(), null, t + 0.1).Removed);
    }

    [Fact]
    public void AfterAReloadOurPitonsThatDidNotComeBackAreWithdrawn()
    {
        var sync = Started(out var t);
        var announced = sync.Reconcile(Placed((1, A), (2, B)), null, t += Settled).Announce;
        var lost = announced.Find(entry => entry.Pointer == new IntPtr(2)).LocalId;

        sync.OnLifeline(ReloadedLifeline, t += 1);
        sync.Reconcile(Placed((11, A)), null, t);
        var actions = sync.Reconcile(Placed((11, A)), null, t + Settled);

        Assert.Equal(new[] { lost }, actions.Removed);
        Assert.Empty(actions.Announce);
    }

    [Fact]
    public void AfterAReloadSavedCopiesOfOthersPitonsAreNotDuplicated()
    {
        var sync = WithLiveRemote(out var t);

        sync.OnLifeline(ReloadedLifeline, t += 1);
        sync.Reconcile(Placed((71, C)), null, t);
        var actions = sync.Reconcile(Placed((71, C)), null, t + Settled);

        Assert.True(actions.IsEmpty);
    }

    [Fact]
    public void AfterAReloadOthersPitonsMissingFromTheSaveAreCreatedAgain()
    {
        var sync = WithLiveRemote(out var t);

        sync.OnLifeline(ReloadedLifeline, t += 1);
        Assert.Empty(sync.Reconcile(Placed(), null, t).Spawn);
        var actions = sync.Reconcile(Placed(), null, t + Settled);

        Assert.Equal(7u, Assert.Single(actions.Spawn).PitonId);
    }

    [Fact]
    public void AfterAReloadSavedCopiesOfPitonsThatAreGoneStayQuiet()
    {
        var sync = Started(out var t);
        sync.Reconcile(Placed((1, A)), null, t += Settled);

        sync.OnLifeline(ReloadedLifeline, t += 1);
        sync.Reconcile(Placed((11, A), (12, C)), null, t);
        var actions = sync.Reconcile(Placed((11, A), (12, C)), null, t += Settled);

        Assert.Empty(actions.Announce);
        Assert.True(sync.Reconcile(Placed((11, A), (12, C)), null, t + 0.1).IsEmpty);
    }

    [Fact]
    public void JoiningRecognisesOurSavedCopiesOfPitonsTheHostAlreadyHas()
    {
        var sync = new PitonSync();
        sync.Know(Piton(7, C));
        sync.OnLifeline(Lifeline, 0);

        sync.Reconcile(Placed((71, C), (1, A)), null, 0);
        var actions = sync.Reconcile(Placed((71, C), (1, A)), null, Settled);

        Assert.Equal(new IntPtr(1), Assert.Single(actions.Announce).Pointer);
        Assert.Empty(actions.Spawn);
    }

    [Fact]
    public void OurPitonTakenByAnotherLeavesWithoutBeingWithdrawnTwice()
    {
        var sync = Started(out var t);
        var id = sync.Reconcile(Placed((1, A)), null, t += Settled).Announce[0].LocalId;

        sync.TakenByOther(id);
        var actions = sync.Reconcile(Placed((1, A)), null, t += 0.1);
        Assert.Equal(new[] { new IntPtr(1) }, actions.Strip);
        Assert.Empty(actions.Detach);
        Assert.Empty(actions.Removed);

        Assert.True(sync.Reconcile(Placed(), null, t + 0.1).IsEmpty);
    }

    [Fact]
    public void AFailedCreationIsRetriedLaterNotEveryFrame()
    {
        var sync = Started(out var t);
        sync.Know(Piton(7, C));
        sync.Reconcile(Placed(), null, t += Settled);
        sync.SpawnFailed(7, t);

        Assert.Empty(sync.Reconcile(Placed(), null, t + 1).Spawn);
        Assert.Single(sync.Reconcile(Placed(), null, t + PitonSync.SpawnRetrySeconds + 1).Spawn);
    }

    [Fact]
    public void DroppingReplicasKeepsOurOwnPiton()
    {
        var sync = WithLiveRemote(out var now);
        var own = Assert.Single(sync.Reconcile(Placed((70, C), (1, A)), null, now += 0.1).Announce).LocalId;
        sync.Ignore(new IntPtr(80));

        var leaving = new List<IntPtr>();
        sync.CopyLeaving(leaving);

        Assert.Contains(new IntPtr(70), leaving);
        Assert.Contains(new IntPtr(80), leaving);
        Assert.DoesNotContain(new IntPtr(1), leaving);

        sync.ForgetReplicas();

        Assert.True(sync.TryGetOwnPointer(own, out var pointer));
        Assert.Equal(new IntPtr(1), pointer);
        Assert.False(sync.TryGetRemotePointer(7, out _));
    }

    [Fact]
    public void ANewSessionNeverReusesOneOfOurIds()
    {
        var sync = Started(out var t);
        var before = sync.Reconcile(Placed((1, A)), null, t += Settled).Announce[0].LocalId;

        sync.ForgetSession();
        sync.OnLifeline(Lifeline, t += 1);
        var after = sync.Reconcile(Placed((1, A)), null, t + Settled).Announce[0].LocalId;

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AReplicaStaysPendingUntilSpawningIsAllowed()
    {
        var sync = Started(out var now);
        sync.SpawnReplicas = false;
        sync.Know(Piton(7, C));
        Assert.Empty(sync.Reconcile(Placed(), null, now += Settled).Spawn);

        sync.SpawnReplicas = true;
        Assert.Equal(7u, Assert.Single(sync.Reconcile(Placed(), null, now + 0.1).Spawn).PitonId);
    }

    [Fact]
    public void AnOwnPitonAndAReplicaReportDifferentIdNamespaces()
    {
        var sync = WithLiveRemote(out var now);
        var announced = sync.Reconcile(Placed((70, C), (2, A)), null, now + 0.1).Announce;

        Assert.True(sync.TryGetId(new IntPtr(2), out var ownId, out var ownLocal));
        Assert.Equal(Assert.Single(announced).LocalId, ownId);
        Assert.True(ownLocal);
        Assert.True(sync.TryGetId(new IntPtr(70), out var hostId, out var hostLocal));
        Assert.Equal(7u, hostId);
        Assert.False(hostLocal);
    }

    [Fact]
    public void AForgottenReplicaSpawnsAgainWhenSpawningIsAllowed()
    {
        var sync = Started(out var now);
        sync.Know(Piton(7, C));
        sync.Reconcile(Placed(), null, now += Settled);
        sync.ForgetReplicas();
        sync.SpawnReplicas = false;
        sync.Know(Piton(7, C));
        Assert.Empty(sync.Reconcile(Placed(), null, now + 0.1).Spawn);

        sync.SpawnReplicas = true;
        Assert.Equal(7u, Assert.Single(sync.Reconcile(Placed(), null, now + 0.2).Spawn).PitonId);
    }

    private static PitonSync Started(out double now)
    {
        now = 0;
        var sync = new PitonSync();
        sync.OnLifeline(Lifeline, now);
        return sync;
    }

    /// <summary>A settled Lifeline holding someone else's piton #7 at C, as pointer 70.</summary>
    private static PitonSync WithLiveRemote(out double now)
    {
        var sync = Started(out now);
        sync.Know(Piton(7, C));
        sync.Reconcile(Placed(), null, now += Settled);
        sync.Spawned(7, new IntPtr(70));
        sync.Reconcile(Placed((70, C)), null, now += 0.1);
        return sync;
    }

    private static List<PlacedPiton> Placed(params (int Pointer, Vector3 Position)[] pitons)
    {
        var placed = new List<PlacedPiton>();
        foreach (var (pointer, position) in pitons) placed.Add(new PlacedPiton(new IntPtr(pointer), position));
        return placed;
    }

    private static ServerPitonPlaced Piton(uint id, Vector3 at)
        => new() { FromPlayerId = 2, PitonId = id, SourcePitonId = id, PosX = at.X, PosY = at.Y, PosZ = at.Z, RotW = 1, Quality = 1, PitonHp = 100, ItemId = 5 };
}
