using System;
using System.Collections.Generic;
using System.Threading;

namespace CairnMultiplayerMod.Internal.Game.Voice;

/// <summary>Managed mixer feeding a streaming OpenAL source on Linux and macOS.</summary>
internal sealed class OpenAlVoiceOutput : IVoiceOutput
{
    private const int BufferFrames = VoiceAdapter.FrameSamples;
    private const int BufferCount = 3;
    private readonly object _sync = new();
    private readonly List<VoicePlayback> _voices = new(9);
    private readonly VoicePlayback[] _snapshot = new VoicePlayback[9];
    private readonly float[] _mix = new float[BufferFrames * 2];
    private readonly float[] _voice = new float[BufferFrames * 2];
    private readonly short[] _pcm = new short[BufferFrames * 2];
    private readonly VoiceOutputLimiter _limiter = new();
    private readonly ManualResetEventSlim _initialized = new();
    private readonly Thread _thread;
    private volatile bool _stopping;
    private volatile bool _running;
    private Exception _failure;
    private long _renderedSamples;

    public long RenderedSamples => Interlocked.Read(ref _renderedSamples);
    public bool IsRunning => _running && !_stopping;

    internal OpenAlVoiceOutput()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "CairnMP OpenAL Voice" };
        _thread.Start();
        if (!_initialized.Wait(TimeSpan.FromSeconds(5)))
        {
            Dispose();
            throw new TimeoutException("OpenAL output initialization timed out.");
        }
        if (_failure != null)
        {
            Dispose();
            throw new InvalidOperationException("OpenAL output initialization failed.", _failure);
        }
    }

    public void Add(VoicePlayback voice)
    {
        if (voice == null) return;
        lock (_sync) if (!_voices.Contains(voice)) _voices.Add(voice);
    }

    public void Remove(VoicePlayback voice)
    {
        lock (_sync) _voices.Remove(voice);
    }

    private void Run()
    {
        OpenAlNative api = null;
        IntPtr device = IntPtr.Zero, context = IntPtr.Zero;
        uint source = 0;
        var buffers = new uint[BufferCount];
        try
        {
            api = OpenAlNative.Instance;
            device = api.OpenDevice(IntPtr.Zero);
            if (device == IntPtr.Zero) throw new InvalidOperationException("OpenAL could not open the default output device.");
            context = api.CreateContext(device, IntPtr.Zero);
            if (context == IntPtr.Zero || api.MakeContextCurrent(context) == 0)
                throw new InvalidOperationException("OpenAL could not create an output context.");
            api.GenSources(1, out source);
            ThrowIfFailed(api, "create source");
            for (var i = 0; i < buffers.Length; i++)
            {
                api.GenBuffers(1, out buffers[i]);
                FillAndQueue(api, source, buffers[i]);
            }
            api.SourcePlay(source);
            ThrowIfFailed(api, "start output");
            _running = true;
            _initialized.Set();

            while (!_stopping)
            {
                api.GetSourceInteger(source, OpenAlNative.BuffersProcessed, out var processed);
                while (processed-- > 0)
                {
                    api.SourceUnqueueBuffers(source, 1, out var buffer);
                    FillAndQueue(api, source, buffer);
                }
                api.GetSourceInteger(source, OpenAlNative.SourceState, out var state);
                if (state != OpenAlNative.Playing) api.SourcePlay(source);
                ThrowIfFailed(api, "stream output");
                Thread.Sleep(4);
            }
        }
        catch (Exception ex)
        {
            _failure = ex;
            _initialized.Set();
        }
        finally
        {
            _running = false;
            if (api != null && source != 0)
            {
                api.SourceStop(source);
                api.DeleteSources(1, ref source);
                for (var i = 0; i < buffers.Length; i++)
                    if (buffers[i] != 0) api.DeleteBuffers(1, ref buffers[i]);
            }
            if (api != null && context != IntPtr.Zero)
            {
                api.MakeContextCurrent(IntPtr.Zero);
                api.DestroyContext(context);
            }
            if (api != null && device != IntPtr.Zero) api.CloseDevice(device);
        }
    }

    private unsafe void FillAndQueue(OpenAlNative api, uint source, uint buffer)
    {
        Array.Clear(_mix, 0, _mix.Length);
        int count;
        lock (_sync)
        {
            count = Math.Min(_voices.Count, _snapshot.Length);
            for (var i = 0; i < count; i++) _snapshot[i] = _voices[i];
        }
        for (var voiceIndex = 0; voiceIndex < count; voiceIndex++)
        {
            Array.Clear(_voice, 0, _voice.Length);
            _snapshot[voiceIndex].Read(_voice, 0, _voice.Length);
            for (var sample = 0; sample < _mix.Length; sample++) _mix[sample] += _voice[sample];
            _snapshot[voiceIndex] = null;
        }
        _limiter.Process(_mix, 0, _mix.Length);
        for (var i = 0; i < _pcm.Length; i++)
            _pcm[i] = (short)Math.Clamp(_mix[i] * 32768f, short.MinValue, short.MaxValue);
        fixed (short* pcm = _pcm)
            api.BufferData(buffer, OpenAlNative.FormatStereo16, (IntPtr)pcm, _pcm.Length * sizeof(short), VoiceAdapter.SampleRate);
        api.SourceQueueBuffers(source, 1, ref buffer);
        Interlocked.Add(ref _renderedSamples, _mix.Length);
    }

    private static void ThrowIfFailed(OpenAlNative api, string operation)
    {
        var error = api.AlGetError();
        if (error != 0) throw new InvalidOperationException($"OpenAL could not {operation} (0x{error:X}).");
    }

    public void Dispose()
    {
        _stopping = true;
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        lock (_sync) _voices.Clear();
    }
}
