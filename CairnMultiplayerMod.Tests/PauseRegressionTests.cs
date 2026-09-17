using CairnMultiplayerMod.Internal.Game;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayer.Shared;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class PauseRegressionTests
{
    [Fact]
    public void MultiplayerPauseRemainsInGameForPoseBroadcasts()
    {
        Assert.Equal(PlayerState.InGame,
            PlayerStateBroadcaster.MapLifecycleForNetwork(
                CairnGameLifecycleState.Menu, pauseMenuActive: true));
        Assert.Equal(PlayerState.InMenu,
            PlayerStateBroadcaster.MapLifecycleForNetwork(
                CairnGameLifecycleState.Menu, pauseMenuActive: false));
        Assert.Equal(PlayerState.Loading,
            PlayerStateBroadcaster.MapLifecycleForNetwork(
                CairnGameLifecycleState.Cutscene, pauseMenuActive: true));
    }

    [Fact]
    public void AdditiveStreamingDoesNotAdvertiseAFalseLoadingTransition()
    {
        Assert.True(PlayerStateBroadcaster.ShouldPreserveInGameDuringStreaming(
            CairnGameLifecycleState.Loading, PlayerState.InGame, hasGameplayContext: true));
        Assert.False(PlayerStateBroadcaster.ShouldPreserveInGameDuringStreaming(
            CairnGameLifecycleState.Loading, PlayerState.InGame, hasGameplayContext: false));
        Assert.False(PlayerStateBroadcaster.ShouldPreserveInGameDuringStreaming(
            CairnGameLifecycleState.Cutscene, PlayerState.InGame, hasGameplayContext: true));
    }

    [Fact]
    public void PauseMenuLifecycleBlocksChatUntilNativeInputContextIsPopped()
    {
        var state = new PauseRequestSuppressionState();

        Assert.False(state.IsPauseMenuActive);

        state.BeginOpening(connected: true);
        Assert.True(state.IsPauseMenuActive);

        state.EndOpening();
        Assert.True(state.IsPauseMenuActive);

        state.BeginClosing();
        Assert.True(state.IsPauseMenuActive);

        state.EndClosing();
        Assert.False(state.IsPauseMenuActive);
    }

    [Fact]
    public void FailedPauseMenuOpeningDoesNotLeaveChatGateActive()
    {
        var state = new PauseRequestSuppressionState();

        state.BeginOpening(connected: true);
        Assert.True(state.SuppressPauseRequest());
        Assert.True(state.SuppressGameTimePauseRequest());
        state.EndOpening(succeeded: false);

        Assert.False(state.IsPauseMenuActive);
        Assert.False(state.SuppressPauseRequest());
        Assert.False(state.SuppressGameTimePauseRequest());

        // Cairn may still run its closing cleanup after a partial opening. Keep the
        // matched unpause suppression so its request counters cannot go negative.
        state.BeginClosing();
        Assert.True(state.SuppressUnpauseRequest());
        Assert.True(state.SuppressGameTimeUnpauseRequest());
        state.EndClosing();
    }

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
