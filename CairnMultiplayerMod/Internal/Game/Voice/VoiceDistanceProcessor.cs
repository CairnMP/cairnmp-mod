using System;

namespace CairnMultiplayerMod.Internal.Game.Voice;

/// <summary>Audio-thread-owned smoothing and low-pass state, independent of Unity's frame rate.</summary>
internal sealed class VoiceDistanceProcessor
{
    private const int MaxInterauralDelaySamples = 29;
    private const int EchoDelaySamples = VoiceAdapter.SampleRate * 130 / 1000;
    private readonly float _step = 1 - MathF.Exp(-1f / (.1f * VoiceAdapter.SampleRate));
    private readonly float[] _directDelay = new float[64];
    private readonly float[] _echoDelay = new float[EchoDelaySamples];
    private float _volume, _pan, _reverb;
    private float _filter = FilterCoefficient(18000);
    private float _previousInput, _previousOutput;
    private int _directDelayPosition, _echoDelayPosition;

    internal void Process(float[] mono, float[] stereo, int offset, float volume, float pan, float cutoff, float reverb = 0)
    {
        volume = float.IsFinite(volume) ? Math.Clamp(volume, 0, 3) : 0;
        pan = float.IsFinite(pan) ? Math.Clamp(pan, -1, 1) : 0;
        reverb = float.IsFinite(reverb) ? Math.Clamp(reverb, 0, .5f) : 0;
        var filter = FilterCoefficient(float.IsFinite(cutoff) ? Math.Clamp(cutoff, 600, 18000) : 18000);
        for (var i = 0; i < mono.Length; i++)
        {
            _volume += (volume - _volume) * _step;
            _pan += (pan - _pan) * _step;
            _reverb += (reverb - _reverb) * _step;
            _filter += (filter - _filter) * _step;
            var input = float.IsFinite(mono[i]) ? mono[i] : 0;
            var sample = _filter * (input + _previousInput) - (2 * _filter - 1) * _previousOutput;
            _previousInput = input;
            _previousOutput = sample;

            _directDelay[_directDelayPosition] = sample;
            var delay = (int)(Math.Abs(_pan) * MaxInterauralDelaySamples);
            var delayedPosition = (_directDelayPosition - delay + _directDelay.Length) % _directDelay.Length;
            var delayed = _directDelay[delayedPosition];
            _directDelayPosition = (_directDelayPosition + 1) % _directDelay.Length;

            var echo = _echoDelay[_echoDelayPosition];
            _echoDelay[_echoDelayPosition] = sample + echo * .28f;
            _echoDelayPosition = (_echoDelayPosition + 1) % _echoDelay.Length;
            VoiceSpatialPolicy.PanGains(_volume, _pan, out var left, out var right);
            var directLeft = _pan > 0 ? delayed : sample;
            var directRight = _pan < 0 ? delayed : sample;
            var wet = echo * _volume * _reverb;
            stereo[offset + i * 2] = directLeft * left + wet * .72f;
            stereo[offset + i * 2 + 1] = directRight * right + wet * .72f;
        }
    }

    private static float FilterCoefficient(float cutoff)
    {
        var k = MathF.Tan(MathF.PI * cutoff / VoiceAdapter.SampleRate);
        return k / (1 + k);
    }
}
