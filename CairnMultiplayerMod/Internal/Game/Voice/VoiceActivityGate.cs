using System;

namespace CairnMultiplayerMod.Internal.Game.Voice;

/// <summary>RMS gate with hysteresis and a 250 ms release to retain quiet syllables.</summary>
internal sealed class VoiceActivityGate
{
    private int _releaseFrames;
    internal float LevelDb { get; private set; } = -90;
    internal bool Process(float[] samples, float thresholdDb)
    {
        double energy = 0;
        foreach (var sample in samples) energy += sample * sample;
        LevelDb = (float)(10 * Math.Log10(Math.Max(1e-9, energy / Math.Max(1, samples.Length))));
        if (LevelDb >= thresholdDb - (_releaseFrames > 0 ? 3 : 0)) _releaseFrames = 13;
        else if (_releaseFrames > 0) _releaseFrames--;
        return _releaseFrames > 0;
    }
    internal void Reset() { _releaseFrames = 0; LevelDb = -90; }
}
