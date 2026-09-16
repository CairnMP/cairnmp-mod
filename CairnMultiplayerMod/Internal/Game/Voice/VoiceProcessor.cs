using System;

namespace CairnMultiplayerMod.Internal.Game.Voice;

/// <summary>Low-latency microphone cleanup designed for one 20 ms mono frame at a time.</summary>
internal sealed class VoiceProcessor
{
    private const float TargetDb = -18f;
    private const float MinimumGainDb = -6f;
    private const float MaximumGainDb = 12f;
    private const float CompressorThresholdDb = -18f;
    private const float CompressorRatio = 3f;
    internal const float LimiterLevel = .8912509f; // -1 dBFS

    private readonly float _highPassAlpha;
    private readonly float _compressorAttack;
    private readonly float _compressorRelease;
    private float _previousInput;
    private float _previousHighPass;
    private float _envelope;
    private float _gainDb;
    private bool _processing;

    internal float InputLevelDb { get; private set; } = -90;
    internal float DetectionLevelDb { get; private set; } = -90;
    internal float OutputLevelDb { get; private set; } = -90;
    internal float GainDb => _gainDb;

    internal VoiceProcessor(int sampleRate = VoiceAdapter.SampleRate)
    {
        var rc = 1f / (2f * MathF.PI * 80f);
        var dt = 1f / sampleRate;
        _highPassAlpha = rc / (rc + dt);
        _compressorAttack = MathF.Exp(-1f / (.005f * sampleRate));
        _compressorRelease = MathF.Exp(-1f / (.100f * sampleRate));
    }

    internal void Process(float[] samples, bool enabled)
    {
        if (samples == null) throw new ArgumentNullException(nameof(samples));
        InputLevelDb = MeasureAndSanitize(samples);
        if (!enabled)
        {
            if (_processing) ResetSignalState();
            _processing = false;
            DetectionLevelDb = InputLevelDb;
            OutputLevelDb = InputLevelDb;
            _gainDb = 0;
            return;
        }

        if (!_processing) ResetSignalState();
        _processing = true;
        for (var i = 0; i < samples.Length; i++)
        {
            var input = samples[i];
            var filtered = _highPassAlpha * (_previousHighPass + input - _previousInput);
            _previousInput = input;
            _previousHighPass = filtered;
            samples[i] = filtered;
        }

        DetectionLevelDb = Measure(samples);
        // Do not amplify an idle noise floor. Speech above -70 dBFS still receives
        // the full configured range and the VAD always sees this pre-gain level.
        var desiredGain = DetectionLevelDb <= -70 ? 0 : Math.Clamp(TargetDb - DetectionLevelDb, MinimumGainDb, MaximumGainDb);
        _gainDb += (desiredGain - _gainDb) * (desiredGain < _gainDb ? .45f : .12f);
        var automaticGain = DbToLinear(_gainDb);

        for (var i = 0; i < samples.Length; i++)
        {
            var amplified = samples[i] * automaticGain;
            var magnitude = MathF.Abs(amplified);
            var coefficient = magnitude > _envelope ? _compressorAttack : _compressorRelease;
            _envelope = coefficient * _envelope + (1 - coefficient) * magnitude;
            var envelopeDb = LinearToDb(_envelope);
            var reductionDb = envelopeDb > CompressorThresholdDb
                ? CompressorThresholdDb + (envelopeDb - CompressorThresholdDb) / CompressorRatio - envelopeDb
                : 0;
            samples[i] = Math.Clamp(amplified * DbToLinear(reductionDb), -LimiterLevel, LimiterLevel);
        }
        OutputLevelDb = Measure(samples);
    }

    internal void Reset()
    {
        ResetSignalState();
        _processing = false;
        InputLevelDb = DetectionLevelDb = OutputLevelDb = -90;
    }

    private void ResetSignalState()
    {
        _previousInput = _previousHighPass = _envelope = _gainDb = 0;
    }

    private static float MeasureAndSanitize(float[] samples)
    {
        for (var i = 0; i < samples.Length; i++)
            if (!float.IsFinite(samples[i])) samples[i] = 0;
        return Measure(samples);
    }

    private static float Measure(float[] samples)
    {
        double energy = 0;
        foreach (var sample in samples) energy += sample * sample;
        return (float)(10 * Math.Log10(Math.Max(1e-9, energy / Math.Max(1, samples.Length))));
    }

    private static float DbToLinear(float db) => MathF.Pow(10, db / 20);
    private static float LinearToDb(float value) => 20 * MathF.Log10(MathF.Max(value, 1e-9f));
}
