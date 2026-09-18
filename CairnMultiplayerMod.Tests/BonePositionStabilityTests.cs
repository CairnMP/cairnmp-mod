using CairnMultiplayerMod.Internal.Diagnostics;
using Xunit;

namespace CairnMultiplayerMod.Tests;

/// <summary>
/// This sampler decides whether half of every NetFrame can stop being sent, so it must not
/// report "constant" on a rig where bones actually move — and must not cry movement over
/// float noise either.
/// </summary>
public sealed class BonePositionStabilityTests
{
    // Root vector first, then two bones.
    private static float[] Frame(float rootX, float boneY, float boneZ)
        => new[] { rootX, 0f, 0f, 0.5f, boneY, 0f, 0.25f, 0f, boneZ };

    [Fact]
    public void OneFrameIsNotEnoughToConclude()
    {
        var probe = new BonePositionStability();
        probe.Sample(Frame(0, 1, 2));

        Assert.Equal(0, probe.FramesCompared);
        Assert.False(probe.PositionsLookConstant);
        Assert.Contains("not enough frames", probe.Describe());
    }

    [Fact]
    public void AMovingRootDoesNotCountAsBoneMovement()
    {
        var probe = new BonePositionStability();
        probe.Sample(Frame(0, 1, 2));
        probe.Sample(Frame(100, 1, 2));   // the player walked; bones unchanged
        probe.Sample(Frame(-40, 1, 2));

        Assert.Equal(2, probe.FramesCompared);
        Assert.Equal(0, probe.MovingComponents);
        Assert.True(probe.PositionsLookConstant);
        Assert.Contains("CONSTANT", probe.Describe());
    }

    [Fact]
    public void ABoneThatMovesIsReported()
    {
        var probe = new BonePositionStability();
        probe.Sample(Frame(0, 1f, 2f));
        probe.Sample(Frame(0, 1.5f, 2f));

        Assert.Equal(1, probe.MovingComponents);
        Assert.Equal(0.5f, probe.LargestDelta, 5);
        Assert.False(probe.PositionsLookConstant);
        Assert.Contains("DO move", probe.Describe());
    }

    [Fact]
    public void FloatNoiseBelowTheEpsilonIsIgnored()
    {
        var probe = new BonePositionStability();
        probe.Sample(Frame(0, 1f, 2f));
        probe.Sample(Frame(0, 1f + BonePositionStability.MovementEpsilon / 2, 2f));

        Assert.Equal(0, probe.MovingComponents);
        Assert.True(probe.PositionsLookConstant);
    }

    [Fact]
    public void EveryMovingComponentIsCountedAndTheLargestKept()
    {
        var probe = new BonePositionStability();
        probe.Sample(Frame(0, 1f, 2f));
        probe.Sample(Frame(0, 1.2f, 2.9f));

        Assert.Equal(2, probe.MovingComponents);
        Assert.Equal(0.9f, probe.LargestDelta, 5);
    }

    [Fact]
    public void ARigThatChangesBoneCountIsNeverDeclaredConstant()
    {
        var probe = new BonePositionStability();
        probe.Sample(Frame(0, 1, 2));
        probe.Sample(new[] { 0f, 0f, 0f, 1f, 1f, 1f }); // different bone count

        Assert.True(probe.SawDifferentLength);
        Assert.False(probe.PositionsLookConstant);
    }

    [Fact]
    public void EmptyOrTinyInputIsIgnoredWithoutThrowing()
    {
        var probe = new BonePositionStability();
        probe.Sample(null);
        probe.Sample(new float[0]);
        probe.Sample(new[] { 1f, 2f });

        Assert.Equal(0, probe.FramesCompared);
    }

    [Fact]
    public void ResetStartsANewMeasurement()
    {
        var probe = new BonePositionStability();
        probe.Sample(Frame(0, 1f, 2f));
        probe.Sample(Frame(0, 9f, 2f));
        probe.Reset();

        Assert.Equal(0, probe.FramesCompared);
        Assert.Equal(0, probe.MovingComponents);
        Assert.False(probe.PositionsLookConstant);
    }

    [Fact]
    public void TheBoneCountIsReported()
    {
        var probe = new BonePositionStability();
        probe.Sample(Frame(0, 1, 2));

        Assert.Equal(3, probe.LastVectorCount);
        Assert.Contains("bones=3", probe.Describe());
    }
}
