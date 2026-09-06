using System;
using Concentus;
using NAudio.Wave;

namespace CairnMultiplayerMod.Internal.Game.Voice;

internal sealed class VoicePlayback : IDisposable, ISampleProvider
{
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(VoiceAdapter.SampleRate, 1);
    private readonly VoiceJitterBuffer _jitter = new();
    private readonly VoiceSampleBuffer _pcm = new(VoiceAdapter.SampleRate / 5);
    private readonly float[] _decoded = new float[VoiceAdapter.FrameSamples];
    private readonly short[] _pcm16 = new short[VoiceAdapter.FrameSamples];
    private float[] _audioScratch = Array.Empty<float>();
    private uint _burst;
    private bool _hasBurst;
    internal volatile float Volume;
    internal volatile float Pan;
    internal double LastReceived { get; private set; }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(VoiceAdapter.SampleRate, 2);
    public int Read(float[] data, int offset, int count)
    {
        // WASAPI thread: no Unity, IL2CPP or codec access here.
        var frames = count / 2;
        if (_audioScratch.Length != frames) _audioScratch = new float[frames];
        _pcm.Read(_audioScratch);
        var volume = Volume;
        var pan = Math.Clamp(Pan, -1, 1);
        var left = volume * MathF.Sqrt((1 - pan) * .5f);
        var right = volume * MathF.Sqrt((1 + pan) * .5f);
        for (var i = 0; i < frames; i++)
        {
            data[offset + i * 2] = _audioScratch[i] * left;
            data[offset + i * 2 + 1] = _audioScratch[i] * right;
        }
        if (count % 2 != 0) data[offset + count - 1] = 0;
        return count;
    }
    internal void Receive(uint burst, uint sequence, byte[] opus, double now)
    {
        if (!_hasBurst || burst != _burst)
        {
            if (_hasBurst && unchecked((int)(burst - _burst)) <= 0) return;
            _burst = burst;
            _hasBurst = true;
            _decoder.ResetState();
            _jitter.Clear();
            _pcm.Clear();
        }
        if (_jitter.Push(sequence, opus, now)) LastReceived = now;
    }
    internal void Tick(double now)
    {
        for (var i = 0; i < 5 && _jitter.TryPop(now, out var opus); i++)
        {
            try
            {
                var count = _decoder.Decode(opus.AsSpan(), _pcm16.AsSpan(), VoiceAdapter.FrameSamples, false);
                for (var sample = 0; sample < count; sample++) _decoded[sample] = _pcm16[sample] / 32768f;
                _pcm.Write(_decoded, count);
            }
            catch (ArgumentException) { _decoder.ResetState(); }
            catch (Concentus.OpusException) { _decoder.ResetState(); }
        }
    }
    internal void WriteLocal(float[] samples) => _pcm.Write(samples, samples.Length);
    public void Dispose()
    {
        Volume = 0;
        _pcm.Clear();
        _decoder.Dispose();
    }
}
