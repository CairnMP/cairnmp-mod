using System;

namespace CairnMultiplayerMod.Internal.Game.Voice;

internal sealed record VoiceDevice(string Id, string Name);

internal interface IVoiceCapture : IDisposable
{
    string DeviceId { get; }
    long CapturedSamples { get; }
    int BufferedSamples { get; }
    bool IsRunning { get; }
    bool TryRead(float[] frame);
    void Clear();
    void KeepLatest(int samples);
}

internal interface IVoiceOutput : IDisposable
{
    long RenderedSamples { get; }
    bool IsRunning { get; }
    void Add(VoicePlayback voice);
    void Remove(VoicePlayback voice);
}

internal enum VoiceBackendKind
{
    Wasapi,
    OpenAl,
}

internal static class VoiceAudioBackend
{
    internal static VoiceBackendKind KindFor(bool isWindows)
        => isWindows ? VoiceBackendKind.Wasapi : VoiceBackendKind.OpenAl;

    internal static VoiceBackendKind Kind => KindFor(OperatingSystem.IsWindows());

    internal static VoiceDevice[] EnumerateCaptureDevices(out string defaultId)
        => Kind == VoiceBackendKind.Wasapi
            ? WindowsVoiceCapture.Enumerate(out defaultId)
            : OpenAlVoiceCapture.Enumerate(out defaultId);

    internal static IVoiceCapture CreateCapture(string id)
        => Kind == VoiceBackendKind.Wasapi
            ? new WindowsVoiceCapture(id)
            : new OpenAlVoiceCapture(id);

    internal static IVoiceOutput CreateOutput()
        => Kind == VoiceBackendKind.Wasapi
            ? new WindowsVoiceOutput()
            : new OpenAlVoiceOutput();
}
