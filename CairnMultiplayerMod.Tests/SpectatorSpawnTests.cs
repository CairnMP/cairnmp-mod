using CairnMultiplayerMod.Internal.Game.Life;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class SpectatorSpawnTests
{
    [Fact]
    public void RememberedViewpointWinsOverALiveCameraFarBelowTheFace()
    {
        // The face from the 2026-11-07 climb, and a hover camera that has followed the
        // ragdoll down through the ground.
        var remembered = SpectatorViewpoint.At(-658.38f, -753.32f, -452.26f, -12f, 80f);
        var live = SpectatorViewpoint.At(-658.38f, -2400f, -452.26f, 0f, 0f);

        var chosen = SpectatorSpawn.Choose(remembered, live);

        Assert.True(chosen.HasPose);
        Assert.Equal(-753.32f, chosen.Y);
        Assert.Equal(-12f, chosen.Pitch);
    }

    [Fact]
    public void AFiniteLiveCameraIsUsedWhenNothingWasRemembered()
    {
        var live = SpectatorViewpoint.At(1f, 2f, 3f, 4f, 5f);

        var chosen = SpectatorSpawn.Choose(default, live);

        Assert.True(chosen.HasPose);
        Assert.Equal(2f, chosen.Y);
        Assert.Equal(5f, chosen.Yaw);
    }

    [Fact]
    public void NoViewpointWhenNeitherPoseIsUsable()
    {
        var broken = SpectatorViewpoint.At(float.NaN, -753f, 0f, 0f, 0f);

        var chosen = SpectatorSpawn.Choose(default, broken);

        Assert.False(broken.HasPose);
        Assert.False(chosen.HasPose);
    }
}
