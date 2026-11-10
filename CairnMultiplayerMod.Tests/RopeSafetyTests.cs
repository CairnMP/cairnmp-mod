using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class RopeSafetyTests
{
    [Fact]
    public void AnAliveClimberInTheWorldKeepsClimbing()
    {
        Assert.Equal(RopeSafetyAction.Continue, Decide(dead: false, inGame: true));
    }

    [Fact]
    public void TheGraceBeforeADecisionDoesNotCutTheRope()
    {
        Assert.Equal(RopeSafetyAction.ReleaseAnchors,
            Decide(dead: true, withinGrace: true, holdingDeath: false));
    }

    [Fact]
    public void AHeldDeathUnclipsOnce()
    {
        Assert.Equal(RopeSafetyAction.UnclipOnce, Decide(dead: true, holdingDeath: true));
        Assert.Equal(RopeSafetyAction.UnclipOnce,
            Decide(dead: true, holdingDeath: true, withinGrace: true));
    }

    [Fact]
    public void ASecondFrameOfTheSameDeathDoesNotUnclipAgain()
    {
        Assert.Equal(RopeSafetyAction.ReleaseAnchors,
            Decide(dead: true, holdingDeath: true, alreadyUnclipped: true));
    }

    [Fact]
    public void ADeathThatPlaysOutTearsTheRopeDown()
    {
        Assert.Equal(RopeSafetyAction.TearDown, Decide(dead: true));
        Assert.Equal(RopeSafetyAction.ReleaseAnchors, Decide(dead: true, alreadyTornDown: true));
    }

    [Fact]
    public void GameOverUsesTheTeardownEvenWhileTheHoldIsStillSet()
    {
        Assert.Equal(RopeSafetyAction.TearDown,
            Decide(dead: true, holdingDeath: true, gameOver: true));
    }

    [Fact]
    public void ADisconnectOrTheMainMenuTearsTheRopeDown()
    {
        Assert.Equal(RopeSafetyAction.TearDown, Decide(connected: false));
        Assert.Equal(RopeSafetyAction.TearDown, Decide(atMainMenu: true));
    }

    [Fact]
    public void LoadingReleasesAnchorsAndLeavesTheLink()
    {
        Assert.Equal(RopeSafetyAction.ReleaseAnchors, Decide(inGame: false));
    }

    private static RopeSafetyAction Decide(
        bool connected = true,
        bool inGame = true,
        bool dead = false,
        bool holdingDeath = false,
        bool withinGrace = false,
        bool gameOver = false,
        bool atMainMenu = false,
        bool alreadyUnclipped = false,
        bool alreadyTornDown = false)
        => RopeSafety.Decide(
            connected, inGame, dead, holdingDeath, withinGrace, gameOver, atMainMenu,
            alreadyUnclipped, alreadyTornDown);
}
