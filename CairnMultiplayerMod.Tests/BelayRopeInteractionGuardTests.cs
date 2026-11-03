using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayRopeInteractionGuardTests
{
    [Theory]
    [InlineData((int)BelayPersonalRopeAction.Grab)]
    [InlineData((int)BelayPersonalRopeAction.Ascend)]
    public void UnownedRopesAndReleasedSessionsKeepNativeInput(int actionValue)
    {
        var action = (BelayPersonalRopeAction)actionValue;
        Assert.False(BelayRopeInteractionGuard.ShouldBlock(action, false, false, true, false, false));
        Assert.False(BelayRopeInteractionGuard.ShouldBlock(action, true, true, true, false, false));
    }

    [Fact]
    public void ClimberCannotSplitTheBelayersRopeThroughAnOrdinaryPersonalGrab()
    {
        Assert.True(BelayRopeInteractionGuard.ShouldBlock(BelayPersonalRopeAction.Grab, true, false, false, false, false));
        Assert.True(BelayRopeInteractionGuard.ShouldBlock(BelayPersonalRopeAction.Grab, true, false, true, false, false));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void UnloadedClippingAndPersonalAnchorMaintenanceRetainTheirNativeRopeHandling(bool clipping, bool anchoring)
        => Assert.False(BelayRopeInteractionGuard.ShouldBlock(BelayPersonalRopeAction.Grab, true, false, false, clipping, anchoring));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ALoadedRopeCannotBeSplitEvenDuringAnInteractionTransition(bool clipping, bool anchoring)
        => Assert.True(BelayRopeInteractionGuard.ShouldBlock(BelayPersonalRopeAction.Grab, true, false, true, clipping, anchoring));

    [Fact]
    public void NativeRopeAscentCannotTakeOverAnActiveBelayerBudget()
        => Assert.True(BelayRopeInteractionGuard.ShouldBlock(BelayPersonalRopeAction.Ascend, true, false, false, true, true));
}
