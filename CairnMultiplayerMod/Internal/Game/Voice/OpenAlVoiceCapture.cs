using System;
using System.Linq;
using System.Threading;

namespace CairnMultiplayerMod.Internal.Game.Voice;

/// <summary>OpenAL capture backend for native Linux and macOS builds.</summary>
internal sealed class OpenAlVoiceCapture : IVoiceCapture
{
    private const string DefaultId = "@openal-default";
    private readonly OpenAlNative _api;
    private readonly VoiceSampleBuffer _buffer = new(VoiceAdapter.SampleRate / 5);
    private readonly short[] _pcm = new short[VoiceAdapter.FrameSamples * 5];
    private readonly float[] _samples = new float[VoiceAdapter.FrameSamples * 5];
    private IntPtr _device;
    private long _sampleCount;

    public string DeviceId { get; }
    public long CapturedSamples => Interlocked.Read(ref _sampleCount);
    public int BufferedSamples { get { Drain(); return _buffer.Count; } }
    public bool IsRunning => _device != IntPtr.Zero;

    internal static VoiceDevice[] Enumerate(out string defaultId)
    {
        var api = OpenAlNative.Instance;
        var defaultName = OpenAlNative.ReadString(api.GetString(IntPtr.Zero, OpenAlNative.CaptureDefaultDeviceSpecifier));
        var names = OpenAlNative.ReadStringList(api.GetString(IntPtr.Zero, OpenAlNative.CaptureDeviceSpecifier));
        if (names.Length == 0)
        {
            defaultId = DefaultId;
            return new[] { new VoiceDevice(DefaultId, string.IsNullOrWhiteSpace(defaultName) ? "System default" : defaultName) };
        }
        defaultId = names.Contains(defaultName, StringComparer.Ordinal) ? defaultName : names[0];
        return names.Select(name => new VoiceDevice(name, name)).ToArray();
    }

    internal OpenAlVoiceCapture(string id)
    {
        _api = OpenAlNative.Instance;
        var useDefault = string.IsNullOrEmpty(id) || id == DefaultId;
        _device = _api.CaptureOpen(useDefault ? null : id);
        if (_device == IntPtr.Zero) throw new InvalidOperationException("OpenAL could not open the selected microphone.");
        DeviceId = useDefault ? DefaultId : id;
        _api.CaptureStart(_device);
        ThrowIfFailed();
    }

    public bool TryRead(float[] frame)
    {
        Drain();
        if (_buffer.Count < frame.Length) return false;
        _buffer.Read(frame);
        return true;
    }

    public void Clear() => _buffer.Clear();
    public void KeepLatest(int samples) { Drain(); _buffer.KeepLatest(samples); }

    private unsafe void Drain()
    {
        if (_device == IntPtr.Zero) return;
        _api.GetInteger(_device, OpenAlNative.CaptureSamplesAvailable, 1, out var available);
        ThrowIfFailed();
        while (available > 0)
        {
            var count = Math.Min(available, _pcm.Length);
            fixed (short* pcm = _pcm) _api.CaptureSamples(_device, (IntPtr)pcm, count);
            ThrowIfFailed();
            for (var i = 0; i < count; i++) _samples[i] = _pcm[i] / 32768f;
            _buffer.Write(_samples, count);
            Interlocked.Add(ref _sampleCount, count);
            available -= count;
        }
    }

    private void ThrowIfFailed()
    {
        var error = _api.GetError(_device);
        if (error != 0) throw new InvalidOperationException($"OpenAL capture failed (0x{error:X}).");
    }

    public void Dispose()
    {
        var device = _device;
        _device = IntPtr.Zero;
        if (device == IntPtr.Zero) return;
        _api.CaptureStop(device);
        _api.CaptureCloseDevice(device);
        _buffer.Clear();
    }
}
