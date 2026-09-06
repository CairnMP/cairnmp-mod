using System;

namespace CairnMultiplayerMod.Internal.Game.Voice;

/// <summary>Bounded PCM queue shared by the game and WASAPI threads. Overflow drops old audio.</summary>
internal sealed class VoiceSampleBuffer
{
    private readonly float[] _samples;
    private readonly object _sync = new();
    private int _read, _count;
    internal VoiceSampleBuffer(int capacity) => _samples = new float[capacity];
    internal int Count { get { lock (_sync) return _count; } }
    internal void WritePcm16(byte[] bytes, int count)
    {
        lock (_sync)
        {
            for (var i = 0; i + 1 < count; i += 2)
            {
                if (_count == _samples.Length) { _read = (_read + 1) % _samples.Length; _count--; }
                var value = (short)(bytes[i] | bytes[i + 1] << 8);
                _samples[(_read + _count++) % _samples.Length] = value / 32768f;
            }
        }
    }
    internal void Write(float[] samples, int count)
    {
        lock (_sync)
        {
            for (var i = 0; i < count; i++)
            {
                if (_count == _samples.Length) { _read = (_read + 1) % _samples.Length; _count--; }
                _samples[(_read + _count++) % _samples.Length] = samples[i];
            }
        }
    }
    internal void Read(float[] target)
    {
        lock (_sync)
        {
            for (var i = 0; i < target.Length; i++)
            {
                target[i] = _count == 0 ? 0 : _samples[_read];
                if (_count == 0) continue;
                _read = (_read + 1) % _samples.Length;
                _count--;
            }
        }
    }
    internal void Clear() { lock (_sync) { _read = 0; _count = 0; } }
}
