using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class BelayPanelKeyGateTests
{
    [Fact]
    public void HoldingTheOpeningKeyDoesNotImmediatelyCloseThePanel()
    {
        var gate = new BelayPanelKeyGate();
        Assert.True(gate.Observe(true));
        Assert.False(gate.Observe(true));
        Assert.False(gate.Observe(true));
    }

    [Fact]
    public void TheNextPressAfterReleaseCanCloseThePanel()
    {
        var gate = new BelayPanelKeyGate();
        gate.Observe(true);
        Assert.False(gate.Observe(false));
        Assert.True(gate.Observe(true));
        Assert.False(gate.Observe(true));
    }

    [Fact]
    public void TwoCompletePressCyclesProduceExactlyTwoToggles()
    {
        var gate = new BelayPanelKeyGate();
        var toggles = 0;
        foreach (var down in new[] { false, true, true, false, false, true, true, false })
            if (gate.Observe(down)) toggles++;
        Assert.Equal(2, toggles);
    }

    [Fact]
    public void AKeyHeldBeforeOpeningThePanelCannotActWhenThePanelOpens()
    {
        var gate = new BelayPanelKeyGate();
        Assert.False(gate.Observe(true, eligible: false));
        Assert.False(gate.Observe(true, eligible: true));
        gate.Observe(false, eligible: true);
        Assert.True(gate.Observe(true, eligible: true));
    }

    [Fact]
    public void ClosingAndReopeningWhileHeldCannotImmediatelyUndoAStationAction()
    {
        var gate = new BelayPanelKeyGate();
        Assert.True(gate.Observe(true, eligible: true));
        Assert.False(gate.Observe(true, eligible: false));
        Assert.False(gate.Observe(true, eligible: true));
        gate.Observe(false, eligible: false);
        Assert.True(gate.Observe(true, eligible: true));
    }
}
