using CairnMultiplayer.Shared;
using Xunit;

namespace CairnMultiplayerShared.Tests;

public sealed class PacketValidationTests
{
    [Fact]
    public void PoseRejectsNonFiniteAndOutOfWorldValues()
    {
        Assert.True(PacketValidation.IsValidPose(1f, 2f, 3f, 90f));
        Assert.False(PacketValidation.IsValidPose(float.NaN, 2f, 3f, 90f));
        Assert.False(PacketValidation.IsValidPose(100001f, 2f, 3f, 90f));
    }

    [Fact]
    public void NetFrameRequiresFiniteAlignedVectorArrays()
    {
        var valid = new NetFrameData
        {
            IsValid = true,
            Positions = new[] { 1f, 2f, 3f },
            Eulers = new[] { 4f, 5f, 6f },
        };

        Assert.True(PacketValidation.IsValidNetFrame(valid, requirePosition: true, out _));

        valid.Eulers = new[] { 1f, 2f, 3f, 4f, 5f, 6f };
        Assert.False(PacketValidation.IsValidNetFrame(valid, requirePosition: true, out var mismatch));
        Assert.Contains("does not match", mismatch);

        valid.Eulers = new[] { 1f, float.PositiveInfinity, 3f };
        Assert.False(PacketValidation.IsValidNetFrame(valid, requirePosition: true, out var nonFinite));
        Assert.Contains("not finite", nonFinite);
    }

    [Fact]
    public void BoneStateRequiresExactFiniteArrays()
    {
        Assert.True(PacketValidation.IsValidBoneState(
            1,
            new[] { 1f, 2f, 3f },
            new[] { 0f, 0f, 0f, 1f }));
        Assert.False(PacketValidation.IsValidBoneState(1, new[] { 1f, 2f }, new float[4]));
        Assert.False(PacketValidation.IsValidBoneState(129, new float[387], new float[516]));
    }

    [Fact]
    public void WeatherRejectsInvalidEnumsAndMagnitudes()
    {
        var weather = ValidWeather();
        Assert.True(PacketValidation.IsValidWeatherState(weather));

        weather.WeatherType = 10;
        Assert.False(PacketValidation.IsValidWeatherState(weather));

        weather = ValidWeather();
        weather.WindForce = float.NaN;
        Assert.False(PacketValidation.IsValidWeatherState(weather));
    }

    [Fact]
    public void PitonRequiresAValidPoseQuaternionAndItem()
    {
        Assert.True(PacketValidation.IsValidPitonPayload(
            1f, 2f, 3f, 0f, 0f, 0f, 1f, quality: 1, hp: 100, itemId: 5));
        Assert.False(PacketValidation.IsValidPitonPayload(
            1f, 2f, 3f, 0f, 0f, 0f, 0f, quality: 1, hp: 100, itemId: 5));
        Assert.False(PacketValidation.IsValidPitonPayload(
            1f, 2f, 3f, 0f, 0f, 0f, 1f, quality: 1, hp: 100, itemId: -1));
    }

    private static WeatherSyncData ValidWeather() => new()
    {
        IsValid = true,
        WeatherType = 1,
        RainType = 1,
        ThunderType = 1,
        FogType = 1,
        CloudsType = 1,
        WindType = 1,
        WindOverride = 1,
        SnowRainForceMode = 1,
        RemainingDuration = 60f,
        UseSnowInsteadOfRain01 = 0.5f,
        WindForce = 5f,
        WindForce01 = 0.5f,
        WindDirX = 1f,
        WindDirY = 0f,
        WindDirZ = 0f,
        WindAngle = 90f,
    };
}
