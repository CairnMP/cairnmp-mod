using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class HarnessRopeDutyTests
{
    [Fact]
    public void PitonOnTheChainStopsTheHarnessPullAndKeepsTheTieAndCoil()
    {
        var duty = HarnessRopeDuty.ForChain(tied: true, alreadyParked: false, pitonCount: 1, coil: 40f);

        Assert.False(duty.Simulate);
        Assert.True(duty.Tied);
        Assert.Equal(40f, duty.Coil);
    }

    [Fact]
    public void EmptyChainStillSimulatesTheHarnessRope()
    {
        var duty = HarnessRopeDuty.ForChain(tied: true, alreadyParked: false, pitonCount: 0, coil: 40f);

        Assert.True(duty.Simulate);
        Assert.True(duty.Tied);
        Assert.Equal(40f, duty.Coil);
    }

    [Fact]
    public void PullStaysOffAfterTheLastPitonLeaves()
    {
        var duty = HarnessRopeDuty.ForChain(tied: true, alreadyParked: true, pitonCount: 0, coil: 40f);

        Assert.False(duty.Simulate);
        Assert.True(duty.Tied);
        Assert.Equal(40f, duty.Coil);
    }
}
