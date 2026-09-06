using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CairnMultiplayerMod.Internal.Game.Voice;

internal sealed record VoiceDevice(string Id, string Name);

/// <summary>Cairn disables Unity Audio for Wwise. WASAPI owns only the mod's audio session.</summary>
internal sealed class WindowsVoiceCapture : IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private readonly VoiceSampleBuffer _buffer = new(VoiceAdapter.SampleRate / 5);
    private volatile Exception _failure;
    private long _sampleCount;
    internal string DeviceId => _device.ID;
    internal long CapturedSamples => Interlocked.Read(ref _sampleCount);
    internal int BufferedSamples => _buffer.Count;
    internal bool IsRunning => _capture.CaptureState == CaptureState.Starting || _capture.CaptureState == CaptureState.Capturing;

    internal static VoiceDevice[] Enumerate(out string defaultId)
    {
        using var enumerator = new MMDeviceEnumerator();
        defaultId = "";
        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            defaultId = device.ID;
        }
        var devices = new List<VoiceDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device) devices.Add(new VoiceDevice(device.ID, device.FriendlyName));
        }
        return devices.ToArray();
    }

    internal WindowsVoiceCapture(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = string.IsNullOrEmpty(id)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : enumerator.GetDevice(id);
        try
        {
            _capture = new WasapiCapture(_device, true, 20) { WaveFormat = new WaveFormat(VoiceAdapter.SampleRate, 16, 1) };
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnStopped;
            _capture.StartRecording();
        }
        catch { _capture?.Dispose(); _device.Dispose(); throw; }
    }
    private void OnData(object sender, WaveInEventArgs args)
    {
        _buffer.WritePcm16(args.Buffer, args.BytesRecorded);
        Interlocked.Add(ref _sampleCount, args.BytesRecorded / 2);
    }
    private void OnStopped(object sender, StoppedEventArgs args) => _failure = args.Exception;
    internal bool TryRead(float[] frame)
    {
        if (_failure != null) throw new InvalidOperationException("Audio input stopped", _failure);
        if (_buffer.Count < frame.Length) return false;
        _buffer.Read(frame);
        return true;
    }
    internal void Clear() => _buffer.Clear();
    public void Dispose()
    {
        _capture.DataAvailable -= OnData;
        _capture.RecordingStopped -= OnStopped;
        _capture.Dispose();
        _buffer.Clear();
        _device.Dispose();
    }
}
