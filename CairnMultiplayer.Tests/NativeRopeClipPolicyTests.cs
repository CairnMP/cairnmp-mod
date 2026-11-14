#nullable enable

using CairnMultiplayerMod.Internal.Networking.Authoritative;
using Xunit;

namespace CairnMultiplayerShared.Tests;

public class NativeRopeClipPolicyTests
{
    [Fact]
    public void TheRecallGateIsOnlyForANewTie()
    {
        Assert.True(NativeRopeClipPolicy.RecallGateApplies(clip: true, resumesHeldPair: false));
        Assert.False(NativeRopeClipPolicy.RecallGateApplies(clip: false, resumesHeldPair: false));
        Assert.False(NativeRopeClipPolicy.RecallGateApplies(clip: true, resumesHeldPair: true));
    }

    [Fact]
    public void TheRecallPromptUsesClimbotAndOnlyShowsBesideAPartner()
    {
        Assert.Equal("Recall Climbot to tie in", NativeRopeClipPolicy.RecallPrompt);
        Assert.Equal(2.5f, NativeRopeClipPolicy.PartnerRecallSeconds);
        Assert.Equal(
            "Ava needs to recall their piton first",
            NativeRopeClipPolicy.PartnerRecallLine("Ava"));
        Assert.Equal(
            "Your partner needs to recall their piton first",
            NativeRopeClipPolicy.PartnerRecallLine("  "));
        Assert.Equal(
            "Ava is not ready to tie in yet",
            NativeRopeClipPolicy.NotReadyLine("Ava"));
        Assert.True(NativeRopeClipPolicy.ShouldPromptRecall(
            alreadyLinked: false, hasTargetInRange: true, inputAvailable: true, selfHoldsNativePiton: true));
        Assert.False(NativeRopeClipPolicy.ShouldPromptRecall(
            alreadyLinked: true, hasTargetInRange: true, inputAvailable: true, selfHoldsNativePiton: true));
        Assert.False(NativeRopeClipPolicy.ShouldPromptRecall(
            alreadyLinked: false, hasTargetInRange: false, inputAvailable: true, selfHoldsNativePiton: true));
        Assert.False(NativeRopeClipPolicy.ShouldPromptRecall(
            alreadyLinked: false, hasTargetInRange: true, inputAvailable: false, selfHoldsNativePiton: true));
        Assert.False(NativeRopeClipPolicy.ShouldPromptRecall(
            alreadyLinked: false, hasTargetInRange: true, inputAvailable: true, selfHoldsNativePiton: false));
    }
}
