#nullable enable

using CairnMultiplayerMod.Internal.Networking.Authoritative;
using Xunit;

namespace CairnMultiplayerShared.Tests;

public class NativeRopeClipPolicyTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ANewTieWaitsWhileEitherClimberStillHoldsANativePiton(bool self, bool partner)
    {
        Assert.True(NativeRopeClipPolicy.BlocksNewTie(
            clip: true, resumesHeldPair: false, self, partner));
    }

    [Fact]
    public void ATieIsAllowedWhenBothNativeRopesAreClear()
    {
        Assert.False(NativeRopeClipPolicy.BlocksNewTie(
            clip: true, resumesHeldPair: false, selfHoldsNativePiton: false, partnerHoldsNativePiton: false));
    }

    [Fact]
    public void UnclipStaysAllowedWhileANativePitonIsOut()
    {
        Assert.False(NativeRopeClipPolicy.BlocksNewTie(
            clip: false, resumesHeldPair: false, selfHoldsNativePiton: true, partnerHoldsNativePiton: true));
    }

    [Fact]
    public void RejoiningAHeldPairIsNotANewTie()
    {
        Assert.False(NativeRopeClipPolicy.BlocksNewTie(
            clip: true, resumesHeldPair: true, selfHoldsNativePiton: true, partnerHoldsNativePiton: true));
    }

    [Fact]
    public void TheRecallPromptShowsOnlyBesideAPartnerWhileAPitonIsStillOut()
    {
        Assert.Equal("Recall piton first", NativeRopeClipPolicy.RecallPrompt);
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

    [Fact]
    public void ANativePitonCannotJoinTheChainOrBecomeAnUnclipSnap()
    {
        Assert.False(NativeRopeClipPolicy.MayJoinChain(sealedAsNative: true));
        Assert.True(NativeRopeClipPolicy.MayJoinChain(sealedAsNative: false));
        Assert.False(NativeRopeClipPolicy.MaySnapOnUnclip(onThisTieChain: true, sealedAsNative: true));
        Assert.False(NativeRopeClipPolicy.MaySnapOnUnclip(onThisTieChain: false, sealedAsNative: false));
        Assert.True(NativeRopeClipPolicy.MaySnapOnUnclip(onThisTieChain: true, sealedAsNative: false));
    }
}
