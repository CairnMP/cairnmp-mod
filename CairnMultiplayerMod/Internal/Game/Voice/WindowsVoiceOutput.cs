using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CairnMultiplayerMod.Internal.Game.Voice;

internal sealed class WindowsVoiceOutput : IVoiceOutput, ISampleProvider
{
    private readonly MixingSampleProvider _mixer = new(WaveFormat.CreateIeeeFloatWaveFormat(VoiceAdapter.SampleRate, 2)) { ReadFully = true };
    private readonly WasapiOut _output;
    private readonly VoiceOutputLimiter _limiter = new();
    private long _renderedSamples;
    public long RenderedSamples => Interlocked.Read(ref _renderedSamples);
    public bool IsRunning => _output.PlaybackState == PlaybackState.Playing;
    public WaveFormat WaveFormat => _mixer.WaveFormat;
    internal WindowsVoiceOutput()
    {
        _output = new WasapiOut(AudioClientShareMode.Shared, false, 40);
        try { _output.Init(this.ToWaveProvider()); _output.Play(); }
        catch { _output.Dispose(); throw; }
    }
    public void Add(VoicePlayback voice) => _mixer.AddMixerInput(voice);
    public void Remove(VoicePlayback voice) => _mixer.RemoveMixerInput(voice);
    public int Read(float[] buffer, int offset, int count)
    {
        var read = _mixer.Read(buffer, offset, count);
        Interlocked.Add(ref _renderedSamples, read);
        _limiter.Process(buffer, offset, read);
        return read;
    }
    public void Dispose() { _output.Stop(); _output.Dispose(); _mixer.RemoveAllMixerInputs(); }
}
