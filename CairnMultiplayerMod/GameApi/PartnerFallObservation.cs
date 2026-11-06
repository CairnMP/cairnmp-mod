using System;

namespace CairnMultiplayerMod.GameApi;

/// <summary>One completed catch reported by the owner of the climbing pawn.</summary>
internal readonly record struct PartnerFallObservation(Guid Session, uint Fall,
    float Height, float Length, float NativeLossFraction);

/// <summary>The host approves a fraction of the receiving player's maximum HP.</summary>
internal readonly record struct PartnerDamageRequest(Guid Session, uint Fall, bool Belayer, float Fraction);

internal readonly record struct PartnerDamageResult(bool Attempted, bool Completed,
    float RequestedFraction, float ObservedFraction, float HpBefore, float HpAfter, string Detail);

/// <summary>Selected game-balance curve shared by native capture and host validation.</summary>
internal static class BelayDamageMath
{
    internal const float BelayerShare = .3f;

    internal static float TargetFraction(float height, float length)
    {
        if (!float.IsFinite(height) || height < 0 || !float.IsFinite(length) || length <= 0 || length > 200)
            return float.NaN;
        var factor = height / length;
        if (!float.IsFinite(factor)) return float.NaN;
        var t = Math.Clamp((height - .5f) / 3.5f, 0, 1);
        var weight = Math.Clamp(t * t * (3 - 2 * t), 0, 1);
        var fraction = factor >= 2 ? .95f
            : factor <= 1.5f ? (.6f / (1.5f * 1.5f)) * factor * factor
                : .6f + (factor - 1.5f) * .7f;
        return Math.Clamp(Math.Clamp(fraction, 0, .95f) * weight, 0, .95f);
    }
}
