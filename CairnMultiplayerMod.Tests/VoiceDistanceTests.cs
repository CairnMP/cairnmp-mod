using System;
using System.Linq;
using CairnMultiplayerMod.Internal.Game.Voice;
using Xunit;

namespace CairnMultiplayerMod.Tests;

public sealed class VoiceDistanceTests
{
    [Fact]
    public void SmoothingDoesNotDependOnAudioBlockSize()
    {
        var mono = Enumerable.Repeat(.25f, 4800).ToArray();
        var whole = new float[mono.Length * 2];
        var pieces = new float[whole.Length];
        var a = new VoiceDistanceProcessor();
        var b = new VoiceDistanceProcessor();
        a.Process(mono, whole, 0, .55f, .7f, 10000);
        for (var i = 0; i < mono.Length; i += 480)
            b.Process(mono.AsSpan(i, 480).ToArray(), pieces, i * 2, .55f, .7f, 10000);
        Assert.Equal(whole, pieces);
    }

    [Fact]
    public void VolumeAndPanStepsRemainSmoothAndFadeReachesSilence()
    {
        var processor = new VoiceDistanceProcessor();
        var mono = Enumerable.Repeat(.25f, 48000).ToArray();
        var output = new float[mono.Length * 2];
        processor.Process(mono, output, 0, 1, -1, 18000);
        var previous = output[^2];
        processor.Process(mono, output, 0, 0, 1, 6000);
        Assert.InRange(Math.Abs(output[0] - previous), 0, .001);
        for (var i = 2; i < output.Length; i += 2)
            Assert.InRange(Math.Abs(output[i] - output[i - 2]), 0, .001);
        Assert.InRange(Math.Abs(output[^2]), 0, .0001);
        Assert.InRange(Math.Abs(output[^1]), 0, .0001);
    }

    [Fact]
    public void FarFilterSoftensHighFrequenciesWhilePreservingSpeechBand()
    {
        static double Energy(float frequency, float cutoff)
        {
            var mono = Enumerable.Range(0, 48000).Select(i => .25f * MathF.Sin(2 * MathF.PI * frequency * i / 48000)).ToArray();
            var stereo = new float[96000];
            new VoiceDistanceProcessor().Process(mono, stereo, 0, 1, 0, cutoff);
            return stereo.Skip(48000).Select(x => (double)x * x).Average();
        }
        Assert.True(Energy(12000, 6000) < Energy(12000, 18000) * .3);
        Assert.True(Energy(1000, 6000) > Energy(1000, 18000) * .9);
    }

    [Fact]
    public void RangeGraceRetainsDecoderAcrossBoundaryOscillations()
    {
        using var playback = new VoicePlayback(true);
        Assert.True(playback.UpdateRange(false, 10));
        Assert.True(playback.UpdateRange(false, 10.9));
        Assert.True(playback.UpdateRange(true, 10.95));
        Assert.True(playback.UpdateRange(false, 11));
        Assert.True(playback.UpdateRange(false, 11.99));
        Assert.False(playback.UpdateRange(false, 12));
    }

    [Fact]
    public void DistanceCurveStaysContinuousAndFilterIsBoundedAcrossTheWalk()
    {
        var lastVolume = 1f;
        var lastCutoff = 18000f;
        for (var i = 0; i <= 350; i++)
        {
            var distance = i / 10f;
            var volume = VoiceSpatialPolicy.Attenuation(distance);
            var cutoff = VoiceSpatialPolicy.Cutoff(distance);
            Assert.InRange(lastVolume - volume, -.00001f, .009f);
            Assert.InRange(cutoff, 5999.99f, 18000);
            Assert.True(cutoff <= lastCutoff + .01f);
            lastVolume = volume; lastCutoff = cutoff;
        }
        Assert.Equal(0, lastVolume);
    }
}
