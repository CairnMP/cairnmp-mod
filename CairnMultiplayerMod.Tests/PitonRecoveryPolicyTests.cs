using System.Numerics;
using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class PitonRecoveryPolicyTests
{
    private static readonly Vector3 Player = Vector3.Zero;

    [Fact]
    public void TakePitonUsesItsOwnKeyInsteadOfOffBelay()
    {
        Assert.Equal("P", PitonRecoverySession.TakeKeyName);
        Assert.Equal("Take piton", PitonRecoverySession.TakeText);
        Assert.Equal("Tap", PitonRecoverySession.TakeCue);
        Assert.NotEqual("E", PitonRecoverySession.TakeKeyName);
    }

    [Fact]
    public void AShortDpadReleaseTakesThePiton()
    {
        var pressedAt = -1f;
        Assert.False(PitonTakeTap.Advance(ref pressedAt, down: true, cancel: false, now: 1f, PitonTakeTap.MaxSeconds));
        Assert.True(PitonTakeTap.Advance(ref pressedAt, down: false, cancel: false,
            now: 1f + PitonTakeTap.MaxSeconds, PitonTakeTap.MaxSeconds));
        Assert.Equal(-1f, pressedAt);
    }

    [Fact]
    public void HoldingTheDpadPastATapDoesNotTake()
    {
        var pressedAt = -1f;
        Assert.False(PitonTakeTap.Advance(ref pressedAt, down: true, cancel: false, now: 1f, PitonTakeTap.MaxSeconds));
        Assert.False(PitonTakeTap.Advance(ref pressedAt, down: false, cancel: false,
            now: 1f + PitonTakeTap.MaxSeconds + 0.01f, PitonTakeTap.MaxSeconds));
        Assert.Equal(-1f, pressedAt);
    }

    [Fact]
    public void AModifierChordDoesNotTake()
    {
        var pressedAt = -1f;
        Assert.False(PitonTakeTap.Advance(ref pressedAt, down: true, cancel: false, now: 1f, PitonTakeTap.MaxSeconds));
        Assert.False(PitonTakeTap.Advance(ref pressedAt, down: true, cancel: true, now: 1.05f, PitonTakeTap.MaxSeconds));
        Assert.Equal(-1f, pressedAt);
    }

    [Fact]
    public void APitonOutOfReachIsLeftAlone()
    {
        var choice = PitonRecoveryPolicy.Choose(new[]
        {
            new PitonRecoveryPolicy.Candidate(new Vector3(0.76f, 0f, 0f), false, true),
        }, Player);

        Assert.Equal(PitonRecoveryPolicy.Decision.None, choice.Decision);
    }

    [Fact]
    public void APitonAtTheEdgeOfReachCanBeTaken()
    {
        var choice = PitonRecoveryPolicy.Choose(new[]
        {
            new PitonRecoveryPolicy.Candidate(new Vector3(PitonRecoveryPolicy.ReachMeters, 0f, 0f), false, true),
        }, Player);

        Assert.Equal(PitonRecoveryPolicy.Decision.Take, choice.Decision);
        Assert.Equal(0, choice.Index);
    }

    [Fact]
    public void ClimbingOrStandingDoesNotRefuseAPitonTheRopeIsHolding()
    {
        Assert.False(PitonRecoveryPolicy.RefusesHeldPiton(PartnerPose.Climbing, heldByRope: true));
        Assert.False(PitonRecoveryPolicy.RefusesHeldPiton(PartnerPose.OnGround, heldByRope: true));
        Assert.False(PitonRecoveryPolicy.RefusesHeldPiton(PartnerPose.Hanging, heldByRope: false));
    }

    [Fact]
    public void HangingRefusesThePitonTheRopeIsHolding()
    {
        Assert.True(PitonRecoveryPolicy.RefusesHeldPiton(PartnerPose.Hanging, heldByRope: true));
    }

    [Fact]
    public void ThePitonHoldingTheClimberIsNotTaken()
    {
        var choice = PitonRecoveryPolicy.Choose(new[]
        {
            new PitonRecoveryPolicy.Candidate(new Vector3(0.3f, 0f, 0f), critical: true, bagCanTake: true),
            new PitonRecoveryPolicy.Candidate(new Vector3(0.6f, 0f, 0f), critical: false, bagCanTake: true),
        }, Player);

        Assert.Equal(PitonRecoveryPolicy.Decision.Critical, choice.Decision);
        Assert.Equal(0, choice.Index);
    }

    [Fact]
    public void AFullBagRefusesTheNearestPiton()
    {
        var choice = PitonRecoveryPolicy.Choose(new[]
        {
            new PitonRecoveryPolicy.Candidate(new Vector3(0.4f, 0f, 0f), false, bagCanTake: false),
        }, Player);

        Assert.Equal(PitonRecoveryPolicy.Decision.BagFull, choice.Decision);
    }

    [Fact]
    public void APlantedStaffIsNotOfferedAsAPiton()
    {
        Assert.Equal(PitonRecoveryPolicy.TakeSide.None,
            PitonRecoveryPolicy.Classify(tracked: false, allowsTake: false, staffPointer: true));
        Assert.Equal(PitonRecoveryPolicy.TakeSide.None,
            PitonRecoveryPolicy.Classify(tracked: true, allowsTake: false, staffPointer: true));
        Assert.Equal(PitonRecoveryPolicy.TakeSide.None,
            PitonRecoveryPolicy.Classify(tracked: true, allowsTake: true, staffPointer: true));
    }

    [Fact]
    public void AnOrdinaryPitonStillGoesToTheClimberWhoMayTakeIt()
    {
        Assert.Equal(PitonRecoveryPolicy.TakeSide.Local,
            PitonRecoveryPolicy.Classify(tracked: false, allowsTake: false, staffPointer: false));
        Assert.Equal(PitonRecoveryPolicy.TakeSide.Local,
            PitonRecoveryPolicy.Classify(tracked: true, allowsTake: true, staffPointer: false));
        Assert.Equal(PitonRecoveryPolicy.TakeSide.PracticeGhost,
            PitonRecoveryPolicy.Classify(tracked: true, allowsTake: false, staffPointer: false));
    }

    [Fact]
    public void TheNearestReachablePitonIsTheOneOffered()
    {
        var choice = PitonRecoveryPolicy.Choose(new[]
        {
            new PitonRecoveryPolicy.Candidate(new Vector3(0.7f, 0f, 0f), false, true),
            new PitonRecoveryPolicy.Candidate(new Vector3(0.2f, 0f, 0f), false, true),
        }, Player);

        Assert.Equal(PitonRecoveryPolicy.Decision.Take, choice.Decision);
        Assert.Equal(1, choice.Index);
    }
}
