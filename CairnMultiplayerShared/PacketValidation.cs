using System;

namespace CairnMultiplayer.Shared;

/// <summary>
/// Pure validation for untrusted protocol values. Keeping these invariants beside the packet
/// contracts lets every transport enforce the same limits without depending on game code.
/// </summary>
public static class PacketValidation
{
    private const float MaxAbsPosition = 100000f;
    private const float MaxAbsWind = 100000f;
    private const int MaxFrameVectorCount = 512;

    public static bool IsValidPose(float x, float y, float z, float yaw)
        => IsFinite(x) && IsFinite(y) && IsFinite(z) && IsFinite(yaw)
           && Math.Abs(x) <= MaxAbsPosition
           && Math.Abs(y) <= MaxAbsPosition
           && Math.Abs(z) <= MaxAbsPosition;

    public static bool IsValidNetFrame(NetFrameData frame, bool requirePosition, out string? reason)
    {
        if (!frame.IsValid)
        {
            reason = "isValid=false";
            return false;
        }
        if (!IsValidVectorArray(frame.Positions, requirePosition, "positions", out reason)) return false;
        if (!IsValidVectorArray(frame.Eulers, required: false, "eulers", out reason)) return false;
        if (frame.Eulers != null && frame.Eulers.Length > 0
            && frame.Positions != null && frame.Eulers.Length != frame.Positions.Length)
        {
            reason = $"eulers length {frame.Eulers.Length} does not match positions length {frame.Positions.Length}";
            return false;
        }

        reason = null;
        return true;
    }

    public static bool IsValidWeatherState(WeatherSyncData state)
    {
        if (!state.IsValid) return false;
        if (!IsInRange(state.WeatherType, 0, 9)
            || !IsInRange(state.RainType, 0, 2)
            || !IsInRange(state.ThunderType, 0, 2)
            || !IsInRange(state.FogType, 0, 1)
            || !IsInRange(state.CloudsType, 0, 2)
            || !IsInRange(state.WindType, 0, 2)
            || !IsInRange(state.WindOverride, 0, 3)
            || !IsInRange(state.SnowRainForceMode, 0, 2))
        {
            return false;
        }

        if (!IsFinite(state.RemainingDuration)
            || !IsFinite(state.UseSnowInsteadOfRain01)
            || !IsFinite(state.WindForce)
            || !IsFinite(state.WindForce01)
            || !IsFinite(state.WindDirX)
            || !IsFinite(state.WindDirY)
            || !IsFinite(state.WindDirZ)
            || !IsFinite(state.WindAngle))
        {
            return false;
        }

        return state.RemainingDuration >= 0f && state.RemainingDuration <= 86400f
               && state.UseSnowInsteadOfRain01 >= 0f && state.UseSnowInsteadOfRain01 <= 1f
               && state.WindForce >= 0f && state.WindForce <= MaxAbsWind
               && state.WindForce01 >= 0f && state.WindForce01 <= 10f
               && Math.Abs(state.WindDirX) <= MaxAbsWind
               && Math.Abs(state.WindDirY) <= MaxAbsWind
               && Math.Abs(state.WindDirZ) <= MaxAbsWind
               && Math.Abs(state.WindAngle) <= MaxAbsWind;
    }

    public static bool IsValidPitonPayload(float x, float y, float z,
        float rotX, float rotY, float rotZ, float rotW, byte quality, int hp, int itemId)
    {
        if (!IsValidPose(x, y, z, 0f)) return false;
        if (!IsFinite(rotX) || !IsFinite(rotY) || !IsFinite(rotZ) || !IsFinite(rotW)) return false;

        var lengthSquared = rotX * rotX + rotY * rotY + rotZ * rotZ + rotW * rotW;
        return lengthSquared > 0.01f && lengthSquared < 4f
               && quality <= 10
               && hp >= 0 && hp <= 100000
               && itemId >= 0;
    }

    public static bool IsValidBoneState(byte boneCount, float[] positions, float[] rotations)
    {
        if (boneCount == 0 || boneCount > 128) return false;
        if (positions == null || rotations == null) return false;
        if (positions.Length != boneCount * 3 || rotations.Length != boneCount * 4) return false;

        foreach (var value in positions)
            if (!IsFinite(value)) return false;
        foreach (var value in rotations)
            if (!IsFinite(value)) return false;
        return true;
    }

    private static bool IsValidVectorArray(float[]? values, bool required, string fieldName, out string? reason)
    {
        if (values == null)
        {
            reason = required ? $"{fieldName}=null" : null;
            return !required;
        }
        if (values.Length % 3 != 0)
        {
            reason = $"{fieldName} length {values.Length} is not a Vector3 array";
            return false;
        }
        if (required && values.Length < 3)
        {
            reason = $"{fieldName} has no root position";
            return false;
        }
        if (values.Length > MaxFrameVectorCount * 3)
        {
            reason = $"{fieldName} has too many vectors ({values.Length / 3})";
            return false;
        }

        for (var index = 0; index < values.Length; index++)
        {
            if (IsFinite(values[index])) continue;
            reason = $"{fieldName}[{index}] is not finite";
            return false;
        }

        reason = null;
        return true;
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool IsInRange(int value, int min, int max) => value >= min && value <= max;
}
