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
        Assert.Equal(
            "Longclimber! needs to recall Climbot first",
            NativeRopeClipPolicy.PartnerRecallLine("Longclimber!"));
        const int row = 46;
        const string steam = "ABCDEFGHIJKLMNOPQRSTUVWXYZ012345";
        Assert.Equal(32, steam.Length);
        var partner = NativeRopeClipPolicy.PartnerRecallLine(steam);
        var notReady = NativeRopeClipPolicy.NotReadyLine(steam);
        var partnerFallback = NativeRopeClipPolicy.PartnerRecallLine("  ");
        var notReadyFallback = NativeRopeClipPolicy.NotReadyLine("  ");
        Assert.Equal("ABCDEFGHI... needs to recall Climbot first", partner);
        Assert.Equal("ABCDEFGHI... isn't ready. Try again.", notReady);
        Assert.Equal("Your partner needs to recall Climbot first", partnerFallback);
        Assert.Equal("Your partner isn't ready. Try again.", notReadyFallback);
        Assert.True(partner.Length <= row);
        Assert.True(notReady.Length <= row);
        Assert.True(partnerFallback.Length <= row);
        Assert.True(notReadyFallback.Length <= row);
        Assert.True(NativeRopeClipPolicy.RecallPrompt.Length <= row);
        Assert.True(NativeRopeClipPolicy.PartnerRecallLine("Ava").Length <= row);
        Assert.True(NativeRopeClipPolicy.NotReadyLine("Ava").Length <= row);
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
