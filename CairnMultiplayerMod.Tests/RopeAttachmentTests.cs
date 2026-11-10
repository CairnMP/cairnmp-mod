using CairnMultiplayerMod.Internal.Game.Roping;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class RopeAttachmentTests
{
    [Theory]
    [InlineData(0f, 1.9f)]
    [InlineData(float.NaN, 1.9f)]
    [InlineData(float.PositiveInfinity, 1.9f)]
    [InlineData(30f, float.NaN)]
    [InlineData(30f, 30f)]
    [InlineData(30f, 242f)]
    public void UninitializedExhaustedOrInvalidRopeCannotStartANativeAttachment(float length, float distance)
        => Assert.False(RopeAttachmentPolicy.CanReach(length, distance));

    [Fact]
    public void AHarnessJumpPastFiveMetersSnapsAndDoesNotUseTheBoundary()
    {
        Assert.False(RopeAttachmentPolicy.NeedsSnap(5f));
        Assert.True(RopeAttachmentPolicy.NeedsSnap(5.01f));
        Assert.False(RopeAttachmentPolicy.NeedsSnap(float.NaN));
    }

    [Fact]
    public void NearbyPartnerWithInitializedRopeCanAttach()
        => Assert.True(RopeAttachmentPolicy.CanReach(30, 1.9f));

    [Theory]
    [InlineData(30f, 1.9f)]
    [InlineData(30f, 20f)]
    [InlineData(30f, 29.9f)]
    public void InitialLengthTracksTheHarnessDistanceButNeverPaysOutTheWholeRope(float maxLength, float distance)
    {
        Assert.True(RopeAttachmentPolicy.TryGetInitialLength(maxLength, distance, out var initialLength));
        Assert.True(initialLength > distance);
        Assert.True(initialLength < maxLength);
    }

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(30f, 30f)]
    [InlineData(float.NaN, 1f)]
    public void InvalidOrExhaustedRopeHasNoInitialLength(float maxLength, float distance)
        => Assert.False(RopeAttachmentPolicy.TryGetInitialLength(maxLength, distance, out _));

    [Theory]
    [InlineData(2f, 2.1f)]
    [InlineData(2f, 2.4f)]
    [InlineData(30f, 32f)]
    public void ASnapHasLandedWhenThePaidOutLengthIsNearTheRequest(float requested, float reported)
        => Assert.True(RopeAttachmentPolicy.SnapLanded(requested, reported));

    [Theory]
    [InlineData(2f, 40f)]
    [InlineData(2f, float.NaN)]
    [InlineData(float.NaN, 2f)]
    [InlineData(0f, 0f)]
    [InlineData(30f, 40f)]
    public void ThePrefabLengthHasNotLanded(float requested, float reported)
        => Assert.False(RopeAttachmentPolicy.SnapLanded(requested, reported));
}
