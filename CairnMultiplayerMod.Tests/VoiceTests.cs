using System;
using System.IO;
using System.Linq;
using CairnMultiplayerMod.Features;
using CairnMultiplayerMod.Internal.Game.Voice;
using Concentus;
using Concentus.Enums;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class VoiceTests
{
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
        var silence = new float[480];
        Assert.False(gate.Process(silence, -40));
        Assert.True(gate.Process(Enumerable.Repeat(.1f, 480).ToArray(), -40));
        for (var i = 0; i < 12; i++) Assert.True(gate.Process(silence, -40));
        Assert.False(gate.Process(silence, -40));
        gate.Process(Enumerable.Repeat(.1f, 480).ToArray(), -40);
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
        using var encoder = OpusCodecFactory.CreateEncoder(24000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        using var decoder = OpusCodecFactory.CreateDecoder(24000, 1);
        encoder.Bitrate = 24000;
        var pcm = Enumerable.Range(0, 480).Select(i => (short)(8192 * Math.Sin(2 * Math.PI * 440 * i / 24000))).ToArray();
        var encoded = new byte[VoiceFrame.MaxBytes];
        // SILK starts with look-ahead; warm up the stream before checking audible energy.
        var warmup = new short[480];
        for (var i = 0; i < 5; i++)
        {
            var size = encoder.Encode(pcm.AsSpan(), 480, encoded.AsSpan(), encoded.Length);
            decoder.Decode(encoded.AsSpan(0, size), warmup.AsSpan(), 480, false);
        }
        var count = encoder.Encode(pcm.AsSpan(), 480, encoded.AsSpan(), encoded.Length);
        var frame = new VoiceFrame { Burst = 9, Sequence = 27, Opus = encoded.Take(count).ToArray() };
        using var wire = new MemoryStream();
        frame.Serialize(new BinaryWriter(wire));
        Assert.True(wire.Length < 1000); // remains below the transport's reliable fallback threshold.
        wire.Position = 0;
        var received = new VoiceFrame();
        received.Deserialize(new BinaryReader(wire));
        Assert.Equal(27u, received.Sequence);
        Assert.Equal(9u, received.Burst);
        var decoded = new short[480];
        Assert.Equal(480, decoder.Decode(received.Opus.AsSpan(), decoded.AsSpan(), 480, false));
        Assert.Contains(decoded, sample => Math.Abs(sample) > 300);
        Assert.Equal(480, decoder.Decode(ReadOnlySpan<byte>.Empty, decoded.AsSpan(), 480, false));
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
