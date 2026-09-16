using System;

namespace CairnMultiplayerMod.Internal.Game.Voice;

/// <summary>Peak limiter applied after all remote voices have been mixed.</summary>
internal sealed class VoiceOutputLimiter
{
    private float _gain = 1;

    internal void Process(float[] samples, int offset, int count)
    {
        var peak = 0f;
        for (var i = offset; i < offset + count; i++)
        {
            if (!float.IsFinite(samples[i])) samples[i] = 0;
            peak = Math.Max(peak, Math.Abs(samples[i]));
        }
        var desired = peak > VoiceProcessor.LimiterLevel ? VoiceProcessor.LimiterLevel / peak : 1;
        if (desired < _gain) _gain = desired;
        else _gain += (1 - _gain) * .02f;
        for (var i = offset; i < offset + count; i++)
            samples[i] = Math.Clamp(samples[i] * _gain, -VoiceProcessor.LimiterLevel, VoiceProcessor.LimiterLevel);
    }

    internal void Reset() => _gain = 1;
}
