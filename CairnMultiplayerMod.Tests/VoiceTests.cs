using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.Internal.Game.Voice;
using Concentus;
using Concentus.Enums;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class VoiceTests
{
    [Fact]
    public void AudioBackendSelectionKeepsWasapiOnWindowsAndUsesOpenAlElsewhere()
    {
        Assert.Equal(VoiceBackendKind.Wasapi, VoiceAudioBackend.KindFor(true));
        Assert.Equal(VoiceBackendKind.OpenAl, VoiceAudioBackend.KindFor(false));
        Assert.Equal(OperatingSystem.IsWindows() ? VoiceBackendKind.Wasapi : VoiceBackendKind.OpenAl,
            VoiceAudioBackend.Kind);
    }

    [Fact]
    public void OpenAlCaptureDeviceListReadsUtf8DoubleNullTerminatedNames()
    {
        var bytes = Encoding.UTF8.GetBytes("Microphone α\0USB Mic\0\0");
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Assert.Equal(new[] { "Microphone α", "USB Mic" }, OpenAlNative.ReadStringList(pointer));
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    [Fact]
    public void BatchedCaptureRetainsAudibleRecentFramesInsteadOfDroppingEveryBatch()
    {
        var buffer = new VoiceSampleBuffer(VoiceAdapter.FrameSamples * 10);
        var frame = new float[VoiceAdapter.FrameSamples];
        // Some drivers/slow game frames deliver 120 ms together. Preserve 100 ms
        // so bounded encoding makes progress on every poll, with no stale backlog.
        for (var batch = 1; batch <= 3; batch++)
        {
            for (var part = 0; part < 6; part++)
                buffer.Write(Enumerable.Repeat(batch * .1f + part * .01f, VoiceAdapter.FrameSamples).ToArray(), VoiceAdapter.FrameSamples);
            buffer.KeepLatest(VoiceAdapter.FrameSamples * 5);
            for (var part = 1; part < 6; part++)
            {
                buffer.Read(frame);
                Assert.All(frame, sample => Assert.Equal(batch * .1f + part * .01f, sample));
            }
            Assert.Equal(0, buffer.Count);
        }
    }

    [Fact]
    public void VoiceFallbackPositionExpiresWhileNativeAvatarIsUnavailable()
    {
        const double packetTime = 1789320000;
        Assert.True(VoiceSpatialPolicy.HasRecentPose(packetTime, packetTime + .1));
        Assert.False(VoiceSpatialPolicy.HasRecentPose(packetTime, packetTime + 3));
        Assert.False(VoiceSpatialPolicy.HasRecentPose(0, packetTime));
        Assert.False(VoiceSpatialPolicy.HasRecentPose(double.NaN, packetTime));
    }

    [Fact]
    public void PlaybackRejectsEarlierBurstsAfterSwitchAndHandlesBurstWrap()
    {
        using var voice = new VoicePlayback();
        voice.Receive(uint.MaxValue, 10, new byte[] { 1 }, 1);
        voice.Receive(0, 11, new byte[] { 1 }, 2);
        Assert.Equal(2, voice.LastReceived);
        voice.Receive(uint.MaxValue, 12, new byte[] { 1 }, 3);
        Assert.Equal(2, voice.LastReceived);
        voice.Receive(0, 12, new byte[] { 1 }, 3);
        Assert.Equal(3, voice.LastReceived);
    }

    [Fact]
    public void PlaybackPansAndMutesWithoutOpeningAnAudioDevice()
    {
        using var voice = new VoicePlayback { Volume = 1, Pan = -1 };
        voice.WriteLocal(new float[] { .5f, -.5f });
        var stereo = new float[4];
        voice.Read(stereo, 0, stereo.Length);
        Assert.Equal(new float[] { .5f, 0, -.5f, 0 }, stereo);
        voice.Volume = 0;
        voice.WriteLocal(new float[] { 1, 1 });
        voice.Read(stereo, 0, stereo.Length);
        Assert.All(stereo, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void CaptureBufferConvertsSignedLittleEndianPcmAndBoundsBacklog()
    {
        var buffer = new VoiceSampleBuffer(2);
        buffer.WritePcm16(new byte[] { 0, 0, 0, 128, 255, 127 }, 6);
        Assert.Equal(2, buffer.Count);
        var pcm = new float[2];
        buffer.Read(pcm);
        Assert.Equal(-1, pcm[0]);
        Assert.InRange(pcm[1], .999f, 1);
    }
    [Fact]
    public void GateKeepsQuietWordEndThenClosesAndResetStopsImmediately()
    {
        var gate = new VoiceActivityGate();
        var silence = new float[VoiceAdapter.FrameSamples];
        Assert.False(gate.Process(silence, -40));
        Assert.True(gate.Process(Enumerable.Repeat(.1f, VoiceAdapter.FrameSamples).ToArray(), -40));
        for (var i = 0; i < 12; i++) Assert.True(gate.Process(silence, -40));
        Assert.False(gate.Process(silence, -40));
        gate.Process(Enumerable.Repeat(.1f, VoiceAdapter.FrameSamples).ToArray(), -40);
        gate.Reset();
        Assert.False(gate.Process(silence, -40));
    }

    [Fact]
    public void JitterReordersPacketsRejectsDuplicatesAndConcealsLoss()
    {
        var jitter = new VoiceJitterBuffer();
        Assert.True(jitter.Push(100, new byte[] { 1 }, 0));
        Assert.True(jitter.Push(102, new byte[] { 3 }, .02));
        Assert.True(jitter.Push(101, new byte[] { 2 }, .04));
        Assert.False(jitter.Push(101, new byte[] { 2 }, .04));
        Assert.False(jitter.TryPop(.05, out _));
        Assert.True(jitter.TryPop(.061, out var a)); Assert.Equal(1, a[0]);
        Assert.True(jitter.TryPop(.081, out var b)); Assert.Equal(2, b[0]);
        Assert.True(jitter.TryPop(.101, out var c)); Assert.Equal(3, c[0]);
        Assert.True(jitter.TryPop(.121, out var missing)); Assert.Null(missing);
        Assert.False(jitter.TryPop(.5, out _));
    }

    [Fact]
    public void JitterHandlesSequenceWrapAndDropsStaleBacklog()
    {
        var jitter = new VoiceJitterBuffer();
        jitter.Push(uint.MaxValue, new byte[] { 1 }, 0);
        jitter.Push(0, new byte[] { 2 }, .02);
        Assert.True(jitter.TryPop(.061, out _));
        Assert.True(jitter.TryPop(.081, out var next)); Assert.Equal(2, next[0]);
        Assert.False(jitter.Push(uint.MaxValue, new byte[] { 1 }, .09));
        Assert.False(jitter.TryPop(2, out _));
        Assert.True(jitter.Push(50, new byte[] { 5 }, 2));
        Assert.True(jitter.TryPop(2.061, out var burst)); Assert.Equal(5, burst[0]);
    }

    [Fact]
    public void AudioBufferDropsOldestOnOverflowAndFillsUnderrunWithSilence()
    {
        var buffer = new VoiceSampleBuffer(4);
        buffer.Write(new float[] { 1, 2, 3, 4, 5, 6 }, 6);
        var output = new float[6];
        buffer.Read(output);
        Assert.Equal(new float[] { 3, 4, 5, 6, 0, 0 }, output);
        buffer.Write(new float[] { 9 }, 1);
        buffer.Clear();
        buffer.Read(output);
        Assert.All(output, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void OpusFrameRoundTripsThroughWireAtTwentyMilliseconds()
    {
        using var encoder = OpusCodecFactory.CreateEncoder(VoiceAdapter.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        using var decoder = OpusCodecFactory.CreateDecoder(VoiceAdapter.SampleRate, 1);
        encoder.Bitrate = 32000;
        encoder.Complexity = 8;
        encoder.UseVBR = true;
        encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        var pcm = Enumerable.Range(0, VoiceAdapter.FrameSamples).Select(i => (short)(8192 * Math.Sin(2 * Math.PI * 440 * i / VoiceAdapter.SampleRate))).ToArray();
        var encoded = new byte[VoiceFrame.MaxBytes];
        // SILK starts with look-ahead; warm up the stream before checking audible energy.
        var warmup = new short[VoiceAdapter.FrameSamples];
        for (var i = 0; i < 5; i++)
        {
            var size = encoder.Encode(pcm.AsSpan(), VoiceAdapter.FrameSamples, encoded.AsSpan(), encoded.Length);
            decoder.Decode(encoded.AsSpan(0, size), warmup.AsSpan(), VoiceAdapter.FrameSamples, false);
        }
        var count = encoder.Encode(pcm.AsSpan(), VoiceAdapter.FrameSamples, encoded.AsSpan(), encoded.Length);
        Assert.InRange(count, 1, VoiceFrame.MaxBytes);
        var frame = new VoiceFrame { Burst = 9, Sequence = 27, Opus = encoded.Take(count).ToArray() };
        using var wire = new MemoryStream();
        frame.Serialize(new BinaryWriter(wire));
        Assert.True(wire.Length <= VoiceFrame.MaxBytes + 10); // remains below the transport's reliable fallback threshold.
        wire.Position = 0;
        var received = new VoiceFrame();
        received.Deserialize(new BinaryReader(wire));
        Assert.Equal(27u, received.Sequence);
        Assert.Equal(9u, received.Burst);
        var decoded = new short[VoiceAdapter.FrameSamples];
        Assert.Equal(VoiceAdapter.FrameSamples, decoder.Decode(received.Opus.AsSpan(), decoded.AsSpan(), VoiceAdapter.FrameSamples, false));
        Assert.Contains(decoded, sample => Math.Abs(sample) > 300);
        Assert.Equal(VoiceAdapter.FrameSamples, decoder.Decode(ReadOnlySpan<byte>.Empty, decoded.AsSpan(), VoiceAdapter.FrameSamples, false));
    }

    [Fact]
    public void MicrophoneEnhancementRaisesQuietSpeechAndLimitsItsPeak()
    {
        var processor = new VoiceProcessor();
        var outputLevel = -90f;
        for (var frameIndex = 0; frameIndex < 30; frameIndex++)
        {
            var samples = Enumerable.Range(0, VoiceAdapter.FrameSamples)
                .Select(i => .02f * MathF.Sin(2 * MathF.PI * 440 * i / VoiceAdapter.SampleRate)).ToArray();
            processor.Process(samples, true);
            outputLevel = processor.OutputLevelDb;
            Assert.All(samples, sample => Assert.InRange(sample, -VoiceProcessor.LimiterLevel, VoiceProcessor.LimiterLevel));
        }
        Assert.InRange(processor.GainDb, 11, 12);
        Assert.True(outputLevel > processor.InputLevelDb + 8);
    }

    [Fact]
    public void MicrophoneEnhancementSuppressesDcAndSanitizesInvalidSamples()
    {
        var processor = new VoiceProcessor();
        var samples = Array.Empty<float>();
        for (var i = 0; i < 20; i++)
        {
            samples = Enumerable.Repeat(.5f, VoiceAdapter.FrameSamples).ToArray();
            if (i == 0) { samples[0] = float.NaN; samples[1] = float.PositiveInfinity; }
            processor.Process(samples, true);
        }
        Assert.All(samples, sample => Assert.True(float.IsFinite(sample)));
        Assert.InRange(processor.OutputLevelDb, -90, -35);
        processor.Reset();
        Assert.Equal(-90, processor.InputLevelDb);
        Assert.Equal(0, processor.GainDb);
    }

    [Fact]
    public void DisabledMicrophoneEnhancementLeavesFiniteAudioUnchanged()
    {
        var processor = new VoiceProcessor();
        var samples = new[] { -.25f, 0, .5f };
        var expected = samples.ToArray();
        processor.Process(samples, false);
        Assert.Equal(expected, samples);
        Assert.Equal(processor.InputLevelDb, processor.OutputLevelDb);
        Assert.Equal(0, processor.GainDb);
    }

    [Fact]
    public void VoiceGateUsesTheLevelBeforeAutomaticGain()
    {
        var processor = new VoiceProcessor();
        var gate = new VoiceActivityGate();
        for (var frameIndex = 0; frameIndex < 30; frameIndex++)
        {
            var samples = Enumerable.Range(0, VoiceAdapter.FrameSamples)
                .Select(i => .005f * MathF.Sin(2 * MathF.PI * 440 * i / VoiceAdapter.SampleRate)).ToArray();
            processor.Process(samples, true);
        }
        Assert.True(processor.DetectionLevelDb < -40);
        Assert.True(processor.OutputLevelDb > -40);
        Assert.False(gate.ProcessLevel(processor.DetectionLevelDb, -40));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 1)]
    [InlineData(12.5, .8)]
    [InlineData(20, .6)]
    [InlineData(25, .475)]
    [InlineData(30, .35)]
    [InlineData(35, .175)]
    [InlineData(40, 0)]
    public void ProximityCurveUsesGentleAudibleSegments(float distance, float expected)
        => Assert.Equal(expected, VoiceSpatialPolicy.Attenuation(distance), 3);

    [Fact]
    public void CenterPanKeepsFullLevelAndHardPanKeepsOneChannel()
    {
        VoiceSpatialPolicy.PanGains(1, 0, out var centerLeft, out var centerRight);
        Assert.Equal(1, centerLeft, 3);
        Assert.Equal(1, centerRight, 3);
        VoiceSpatialPolicy.PanGains(1, -1, out var left, out var right);
        Assert.Equal(1, left, 3);
        Assert.Equal(0, right, 3);
    }

    [Fact]
    public void ReverberantZoneCarriesAQuietTailBeyondNormalRange()
    {
        Assert.Equal(0, VoiceSpatialPolicy.Attenuation(50), 3);
        Assert.InRange(VoiceSpatialPolicy.Attenuation(50, VoiceSpatialPolicy.ReverberantMaxDistance), .11f, .13f);
        Assert.True(VoiceSpatialPolicy.Reverb(50, .3f, VoiceSpatialPolicy.ReverberantMaxDistance) > .25f);
        Assert.Equal(0, VoiceSpatialPolicy.Attenuation(70, VoiceSpatialPolicy.ReverberantMaxDistance));
    }

    [Fact]
    public void FinalVoiceMixLimiterPreventsMultipleSpeakersFromClipping()
    {
        var limiter = new VoiceOutputLimiter();
        var mix = new[] { -2.4f, 1.8f, .5f, float.NaN };
        limiter.Process(mix, 0, mix.Length);
        Assert.All(mix, sample => Assert.InRange(sample, -VoiceProcessor.LimiterLevel, VoiceProcessor.LimiterLevel));
        Assert.Equal(0, mix[3]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(401, 401)]
    [InlineData(20, 19)]
    [InlineData(20, 21)]
    public void MalformedVoiceFramesAreRejected(int declared, int actual)
    {
        using var wire = new MemoryStream();
        var writer = new BinaryWriter(wire);
        writer.Write(0u); writer.Write(0u); writer.Write((ushort)declared); writer.Write(new byte[actual]);
        wire.Position = 0;
        Assert.Throws<InvalidDataException>(() => new VoiceFrame().Deserialize(new BinaryReader(wire)));
    }
}
