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
        var emoji = char.ConvertFromUtf32(0x1F600);
        Assert.Equal(2, emoji.Length);
        var split = "ABCDEFGH" + emoji + new string('Z', 22);
        Assert.Equal(32, split.Length);
        Assert.True(char.IsHighSurrogate(split[8]));
        var splitPartner = NativeRopeClipPolicy.PartnerRecallLine(split);
        var splitReady = NativeRopeClipPolicy.NotReadyLine(split);
        Assert.Equal("ABCDEFGH... needs to recall Climbot first", splitPartner);
        Assert.Equal("ABCDEFGH... isn't ready. Try again.", splitReady);
        Assert.True(splitPartner.Length <= row);
        Assert.True(splitReady.Length <= row);
        AssertNoLoneSurrogate(splitPartner);
        AssertNoLoneSurrogate(splitReady);
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

    private static void AssertNoLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            Assert.True(char.IsHighSurrogate(text[i]));
            Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]));
            i++;
        }
    }
}
