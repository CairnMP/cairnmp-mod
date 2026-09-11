using CairnMultiplayerMod.Internal.Game;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class PauseRegressionTests
{
    [Fact]
    public void MultiplayerPauseSuppressesMatchedNativePauseAndUnpauseRequests()
    {
        var state = new PauseRequestSuppressionState();

        state.BeginOpening(connected: true);
        Assert.True(state.SuppressPauseRequest());
        Assert.True(state.SuppressGameTimePauseRequest());
        state.EndOpening();

        Assert.False(state.SuppressPauseRequest());
        Assert.False(state.SuppressGameTimePauseRequest());

        state.BeginClosing();
        Assert.True(state.SuppressUnpauseRequest());
        Assert.True(state.SuppressGameTimeUnpauseRequest());
        state.EndClosing();

        state.BeginClosing();
        Assert.False(state.SuppressUnpauseRequest());
        Assert.False(state.SuppressGameTimeUnpauseRequest());
    }

    [Fact]
    public void SoloPauseLeavesAllNativeRequestsUntouched()
    {
        var state = new PauseRequestSuppressionState();

        state.BeginOpening(connected: false);
        Assert.False(state.SuppressPauseRequest());
        Assert.False(state.SuppressGameTimePauseRequest());
        state.EndOpening();

        state.BeginClosing();
        Assert.False(state.SuppressUnpauseRequest());
        Assert.False(state.SuppressGameTimeUnpauseRequest());
    }

    [Fact]
    public void ClosingOnlySuppressesTheRequestKindsActuallySkippedWhileOpening()
    {
        var state = new PauseRequestSuppressionState();

        state.BeginOpening(connected: true);
        Assert.True(state.SuppressPauseRequest());
        state.EndOpening();

        state.BeginClosing();
        Assert.True(state.SuppressUnpauseRequest());
        Assert.False(state.SuppressGameTimeUnpauseRequest());
    }
}
