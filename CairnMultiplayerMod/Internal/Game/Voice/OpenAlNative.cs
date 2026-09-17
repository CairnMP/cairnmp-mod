using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace CairnMultiplayerMod.Internal.Game.Voice;

/// <summary>Small dynamically loaded OpenAL 1.1 surface used on Linux and macOS.</summary>
internal sealed class OpenAlNative
{
    internal const int FormatMono16 = 0x1101;
    internal const int FormatStereo16 = 0x1103;
    internal const int SourceState = 0x1010;
    internal const int Playing = 0x1012;
    internal const int BuffersProcessed = 0x1016;
    internal const int CaptureDeviceSpecifier = 0x310;
    internal const int CaptureDefaultDeviceSpecifier = 0x311;
    internal const int CaptureSamplesAvailable = 0x312;

    private static readonly Lazy<OpenAlNative> InstanceValue = new(() => new OpenAlNative());
    internal static OpenAlNative Instance => InstanceValue.Value;

    private readonly IntPtr _library;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr OpenDeviceDelegate(IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate byte CloseDeviceDelegate(IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr CreateContextDelegate(IntPtr device, IntPtr attributes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void DestroyContextDelegate(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate byte MakeContextCurrentDelegate(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr GetStringDelegate(IntPtr device, int parameter);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int GetErrorDelegate(IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void GetIntegerDelegate(IntPtr device, int parameter, int size, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr CaptureOpenDeviceDelegate(IntPtr name, uint frequency, int format, int bufferSize);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate byte CaptureCloseDeviceDelegate(IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void CaptureControlDelegate(IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void CaptureSamplesDelegate(IntPtr device, IntPtr buffer, int samples);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void GenOneDelegate(int count, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void DeleteOneDelegate(int count, ref uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void BufferDataDelegate(uint buffer, int format, IntPtr data, int size, int frequency);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void SourceQueueDelegate(uint source, int count, ref uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void SourceUnqueueDelegate(uint source, int count, out uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void SourceControlDelegate(uint source);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void GetSourceIntegerDelegate(uint source, int parameter, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int AlGetErrorDelegate();

    internal readonly OpenDeviceDelegate OpenDevice;
    internal readonly CloseDeviceDelegate CloseDevice;
    internal readonly CreateContextDelegate CreateContext;
    internal readonly DestroyContextDelegate DestroyContext;
    internal readonly MakeContextCurrentDelegate MakeContextCurrent;
    internal readonly GetStringDelegate GetString;
    internal readonly GetErrorDelegate GetError;
    internal readonly GetIntegerDelegate GetInteger;
    internal readonly CaptureOpenDeviceDelegate CaptureOpenDevice;
    internal readonly CaptureCloseDeviceDelegate CaptureCloseDevice;
    internal readonly CaptureControlDelegate CaptureStart;
    internal readonly CaptureControlDelegate CaptureStop;
    internal readonly CaptureSamplesDelegate CaptureSamples;
    internal readonly GenOneDelegate GenBuffers;
    internal readonly DeleteOneDelegate DeleteBuffers;
    internal readonly GenOneDelegate GenSources;
    internal readonly DeleteOneDelegate DeleteSources;
    internal readonly BufferDataDelegate BufferData;
    internal readonly SourceQueueDelegate SourceQueueBuffers;
    internal readonly SourceUnqueueDelegate SourceUnqueueBuffers;
    internal readonly SourceControlDelegate SourcePlay;
    internal readonly SourceControlDelegate SourceStop;
    internal readonly GetSourceIntegerDelegate GetSourceInteger;
    internal readonly AlGetErrorDelegate AlGetError;

    private OpenAlNative()
    {
        _library = LoadLibrary();
        OpenDevice = Load<OpenDeviceDelegate>("alcOpenDevice");
        CloseDevice = Load<CloseDeviceDelegate>("alcCloseDevice");
        CreateContext = Load<CreateContextDelegate>("alcCreateContext");
        DestroyContext = Load<DestroyContextDelegate>("alcDestroyContext");
        MakeContextCurrent = Load<MakeContextCurrentDelegate>("alcMakeContextCurrent");
        GetString = Load<GetStringDelegate>("alcGetString");
        GetError = Load<GetErrorDelegate>("alcGetError");
        GetInteger = Load<GetIntegerDelegate>("alcGetIntegerv");
        CaptureOpenDevice = Load<CaptureOpenDeviceDelegate>("alcCaptureOpenDevice");
        CaptureCloseDevice = Load<CaptureCloseDeviceDelegate>("alcCaptureCloseDevice");
        CaptureStart = Load<CaptureControlDelegate>("alcCaptureStart");
        CaptureStop = Load<CaptureControlDelegate>("alcCaptureStop");
        CaptureSamples = Load<CaptureSamplesDelegate>("alcCaptureSamples");
        GenBuffers = Load<GenOneDelegate>("alGenBuffers");
        DeleteBuffers = Load<DeleteOneDelegate>("alDeleteBuffers");
        GenSources = Load<GenOneDelegate>("alGenSources");
        DeleteSources = Load<DeleteOneDelegate>("alDeleteSources");
        BufferData = Load<BufferDataDelegate>("alBufferData");
        SourceQueueBuffers = Load<SourceQueueDelegate>("alSourceQueueBuffers");
        SourceUnqueueBuffers = Load<SourceUnqueueDelegate>("alSourceUnqueueBuffers");
        SourcePlay = Load<SourceControlDelegate>("alSourcePlay");
        SourceStop = Load<SourceControlDelegate>("alSourceStop");
        GetSourceInteger = Load<GetSourceIntegerDelegate>("alGetSourcei");
        AlGetError = Load<AlGetErrorDelegate>("alGetError");
    }

    internal IntPtr CaptureOpen(string name)
        => WithUtf8(name, pointer => CaptureOpenDevice(pointer, VoiceAdapter.SampleRate, FormatMono16,
            VoiceAdapter.SampleRate / 5));

    internal static string ReadString(IntPtr pointer)
        => pointer == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(pointer) ?? "";

    internal static string[] ReadStringList(IntPtr pointer)
    {
        var result = new List<string>();
        if (pointer == IntPtr.Zero) return result.ToArray();
        var offset = 0;
        while (Marshal.ReadByte(pointer, offset) != 0)
        {
            var bytes = new List<byte>();
            while (Marshal.ReadByte(pointer, offset) != 0) bytes.Add(Marshal.ReadByte(pointer, offset++));
            offset++;
            var name = Encoding.UTF8.GetString(bytes.ToArray());
            if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
        }
        return result.ToArray();
    }

    private T Load<T>(string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private static IntPtr LoadLibrary()
    {
        var candidates = OperatingSystem.IsMacOS()
            ? new[] { "/System/Library/Frameworks/OpenAL.framework/OpenAL", "libopenal.dylib" }
            : new[] { "libopenal.so.1", "libopenal.so" };
        foreach (var candidate in candidates)
            if (NativeLibrary.TryLoad(candidate, out var library)) return library;
        var expected = OperatingSystem.IsMacOS()
            ? "the macOS OpenAL framework"
            : "OpenAL Soft (libopenal.so.1)";
        throw new DllNotFoundException($"OpenAL was not found. Install {expected} and restart the game.");
    }

    private static T WithUtf8<T>(string value, Func<IntPtr, T> action)
    {
        if (string.IsNullOrEmpty(value)) return action(IntPtr.Zero);
        var pointer = Marshal.StringToCoTaskMemUTF8(value);
        try { return action(pointer); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
}
