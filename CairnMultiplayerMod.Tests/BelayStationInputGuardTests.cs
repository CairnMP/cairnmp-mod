using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayStationInputGuardTests
{
    private sealed class ActionState
    {
        internal long Id = 1;
        internal bool Enabled = true, FailDisable, FailEnable, PressedThisFrame;
        internal int Disables, Enables, Resets;
    }

    private static BelayStationInputGuard<ActionState> Guard()
        => new(action => action.Id, action => action.Enabled,
            action =>
            {
                action.Disables++; action.Enabled = false;
                if (action.FailDisable) throw new InvalidOperationException("Disable failed after changing state.");
            },
            action =>
            {
                action.Enables++;
                if (action.FailEnable) throw new InvalidOperationException("Enable failed.");
                action.Enabled = true;
            },
            action => { action.Resets++; action.PressedThisFrame = false; });

    [Fact]
    public void BelayerOwnsOnlyTheClimbActionItDisabled()
    {
        var guard = Guard();
        var climb = new ActionState();
        var camera = new ActionState { Id = 2 };
        Assert.True(guard.TrySuppress(climb));
        Assert.False(climb.Enabled);
        Assert.True(camera.Enabled);
        Assert.False(guard.TrySuppress(camera));
        Assert.True(guard.TryRelease(true));
        Assert.True(climb.Enabled);
        Assert.Equal(0, camera.Disables);
        Assert.Equal(0, camera.Enables);
        Assert.Equal(0, camera.Resets);
    }

    [Fact]
    public void AlreadyDisabledActionDoesNotBecomeOursToEnable()
    {
        var guard = Guard();
        var climb = new ActionState { Enabled = false };
        Assert.True(guard.TrySuppress(climb));
        Assert.False(guard.HasOwnership);
        Assert.True(guard.TryRelease(true));
        Assert.False(climb.Enabled);
        Assert.Equal(0, climb.Enables);
    }

    [Fact]
    public void AMapRefreshIsSuppressedAgainWithoutRepeatedDisableCalls()
    {
        var guard = Guard();
        var climb = new ActionState();
        Assert.True(guard.TrySuppress(climb));
        Assert.True(guard.TrySuppress(climb));
        Assert.Equal(1, climb.Disables);
        climb.Enabled = true; // Native context/map rebuild after closing L.
        Assert.True(guard.TrySuppress(climb));
        Assert.Equal(2, climb.Disables);
        Assert.True(guard.TryRelease(true));
        Assert.True(climb.Enabled);
    }

    [Fact]
    public void StartingInsideThePanelStillAcquiresOwnershipAfterTheMapReopens()
    {
        var guard = Guard();
        var climb = new ActionState { Enabled = false };
        Assert.True(guard.TrySuppress(climb));
        climb.Enabled = true;
        Assert.True(guard.TrySuppress(climb));
        Assert.True(guard.HasOwnership);
        Assert.True(guard.TryRelease(true));
        Assert.True(climb.Enabled);
    }

    [Fact]
    public void APressSampledBeforeDisableCannotExitTheStationAfterAMapRefresh()
    {
        var guard = Guard();
        var climb = new ActionState { PressedThisFrame = true };
        // Unity's Disable changes enabled but retains this frame's press flag.
        Assert.True(guard.TrySuppress(climb));
        Assert.False(climb.PressedThisFrame);
        Assert.False(climb.Enabled);
        climb.Enabled = true;
        climb.PressedThisFrame = true;
        Assert.True(guard.TrySuppress(climb));
        Assert.False(climb.PressedThisFrame);
        Assert.False(climb.Enabled);
        Assert.Equal(2, climb.Resets);
        Assert.True(guard.TryRelease(true));
        Assert.True(climb.Enabled);
    }

    [Fact]
    public void AnAlreadyDisabledActionHasItsSampledPressClearedWithoutRestoreOwnership()
    {
        var guard = Guard();
        var climb = new ActionState { Enabled = false, PressedThisFrame = true };
        Assert.True(guard.TrySuppress(climb));
        Assert.False(climb.PressedThisFrame);
        Assert.False(guard.HasOwnership);
        Assert.True(guard.TryRelease(true));
        Assert.False(climb.Enabled);
        Assert.Equal(0, climb.Disables);
        Assert.Equal(0, climb.Enables);
        Assert.Equal(1, climb.Resets);
    }

    [Fact]
    public void EndBelayDefersRestoreUntilTheModalClosesThenReleasesOnce()
    {
        var guard = Guard();
        var climb = new ActionState();
        guard.TrySuppress(climb);
        Assert.False(guard.TryRelease(false));
        Assert.False(guard.TryRelease(false));
        Assert.True(guard.HasOwnership);
        Assert.False(climb.Enabled);
        Assert.Equal(0, climb.Enables);
        Assert.True(guard.TryRelease(true));
        Assert.True(guard.TryRelease(true));
        Assert.False(guard.HasOwnership);
        Assert.Equal(1, climb.Enables);
    }

    [Fact]
    public void AnEnableFailureRetainsOwnershipForTheNextCleanupTick()
    {
        var guard = Guard();
        var climb = new ActionState { FailEnable = true };
        guard.TrySuppress(climb);
        Assert.Throws<InvalidOperationException>(() => guard.TryRelease(true));
        Assert.True(guard.HasOwnership);
        climb.FailEnable = false;
        Assert.True(guard.TryRelease(true));
        Assert.True(climb.Enabled);
        Assert.False(guard.HasOwnership);
    }

    [Fact]
    public void ADisableFailureAfterChangingStateStillHasARestoreOwner()
    {
        var guard = Guard();
        var climb = new ActionState { FailDisable = true };
        Assert.Throws<InvalidOperationException>(() => guard.TrySuppress(climb));
        Assert.True(guard.HasOwnership);
        Assert.True(guard.TryRelease(true));
        Assert.True(climb.Enabled);
    }

    [Fact]
    public void ReleasedBelayerCanBecomeClimberAndALaterStationCanOwnANewAction()
    {
        var guard = Guard();
        var first = new ActionState();
        var next = new ActionState { Id = 2 };
        guard.TrySuppress(first);
        Assert.True(guard.TryRelease(true));
        Assert.True(first.Enabled);
        Assert.True(guard.TrySuppress(next));
        Assert.False(next.Enabled);
        Assert.True(guard.TryRelease(true));
        Assert.True(next.Enabled);
    }

    [Fact]
    public void SceneTeardownDiscardsAFailedOldActionBeforeTheNextInputAssetIsUsed()
    {
        var guard = Guard();
        var destroyed = new ActionState { FailEnable = true };
        var next = new ActionState { Id = 2 };
        guard.TrySuppress(destroyed);
        Assert.Throws<InvalidOperationException>(() => guard.TryRelease(true));
        guard.DiscardOwnership();
        Assert.False(guard.HasOwnership);
        Assert.True(guard.TrySuppress(next));
        Assert.False(next.Enabled);
        Assert.True(guard.TryRelease(true));
        Assert.True(next.Enabled);
    }
}
