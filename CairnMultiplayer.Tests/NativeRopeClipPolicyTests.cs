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
        Assert.Equal(2.5f, NativeRopeClipPolicy.TieRefusalSeconds);
        Assert.Equal(
            "Ava needs to recall Climbot first",
            NativeRopeClipPolicy.PartnerRecallLine("Ava"));
        Assert.Equal(
            "Your partner needs to recall Climbot first",
            NativeRopeClipPolicy.PartnerRecallLine("  "));
        Assert.Equal(
            "Ava isn't ready. Try again.",
            NativeRopeClipPolicy.NotReadyLine("Ava"));
        // The old partner fallback is the longest line the row already fits.
        const int row = 46;
        const string twelve = "Longclimber!";
        Assert.Equal(12, twelve.Length);
        Assert.Equal(12, "Your partner".Length);
        var partner = NativeRopeClipPolicy.PartnerRecallLine(twelve);
        var partnerFallback = NativeRopeClipPolicy.PartnerRecallLine("  ");
        var notReady = NativeRopeClipPolicy.NotReadyLine(twelve);
        var notReadyFallback = NativeRopeClipPolicy.NotReadyLine("  ");
        Assert.True(partner.Length <= row);
        Assert.True(partnerFallback.Length <= row);
        Assert.True(notReady.Length <= row);
        Assert.True(notReadyFallback.Length <= row);
        Assert.True(NativeRopeClipPolicy.RecallPrompt.Length <= row);
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
