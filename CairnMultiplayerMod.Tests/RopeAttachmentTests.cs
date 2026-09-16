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
    public void NearbyPartnerWithInitializedRopeCanAttach()
        => Assert.True(RopeAttachmentPolicy.CanReach(30, 1.9f));
}
