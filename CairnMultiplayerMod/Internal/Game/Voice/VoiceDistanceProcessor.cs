using System;

namespace CairnMultiplayerMod.Internal.Game.Voice;

/// <summary>Audio-thread-owned smoothing and low-pass state, independent of Unity's frame rate.</summary>
internal sealed class VoiceDistanceProcessor
{
    private readonly float _step = 1 - MathF.Exp(-1f / (.1f * VoiceAdapter.SampleRate));
    private float _volume, _pan;
    private float _filter = FilterCoefficient(18000);
    private float _previousInput, _previousOutput;

    internal void Process(float[] mono, float[] stereo, int offset, float volume, float pan, float cutoff)
    {
        volume = float.IsFinite(volume) ? Math.Clamp(volume, 0, 3) : 0;
        pan = float.IsFinite(pan) ? Math.Clamp(pan, -1, 1) : 0;
        var filter = FilterCoefficient(float.IsFinite(cutoff) ? Math.Clamp(cutoff, 6000, 18000) : 18000);
        for (var i = 0; i < mono.Length; i++)
        {
            _volume += (volume - _volume) * _step;
            _pan += (pan - _pan) * _step;
            _filter += (filter - _filter) * _step;
            var input = float.IsFinite(mono[i]) ? mono[i] : 0;
            var sample = _filter * (input + _previousInput) - (2 * _filter - 1) * _previousOutput;
            _previousInput = input;
            _previousOutput = sample;
            VoiceSpatialPolicy.PanGains(_volume, _pan, out var left, out var right);
            stereo[offset + i * 2] = sample * left;
            stereo[offset + i * 2 + 1] = sample * right;
        }
    }

    private static float FilterCoefficient(float cutoff)
    {
        var k = MathF.Tan(MathF.PI * cutoff / VoiceAdapter.SampleRate);
        return k / (1 + k);
    }
}
