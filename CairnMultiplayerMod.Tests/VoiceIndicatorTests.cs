using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Internal.Game.Voice;
using Xunit;

namespace CairnMultiplayerMod.Tests;

/// <summary>
/// The speaking icon is derived from the audio already received, and prefixed onto the
/// floating name. Both rules are pinned here: the hold window that keeps the icon from
/// flickering between packets, and a label that never shifts the name sideways.
/// </summary>
public sealed class VoiceIndicatorTests
{
    private const double Now = 1000d;

    [Fact]
    public void APlayerHeardJustNowIsSpeaking()
        => Assert.True(VoiceIndicatorPolicy.IsSpeaking(Now, Now));

    [Fact]
    public void TheIconHoldsBetweenTwoPackets()
    {
        // Opus packets arrive every 20-60 ms; the icon must not blink in the gap.
        Assert.True(VoiceIndicatorPolicy.IsSpeaking(Now - 0.06, Now));
        Assert.True(VoiceIndicatorPolicy.IsSpeaking(Now - 0.25, Now));
    }

    [Fact]
    public void TheIconClearsShortlyAfterSomeoneStops()
    {
        // Just past the hold window, and long past it. The exact boundary is deliberately
        // not asserted: it carries no meaning, and floating point owns it.
        Assert.False(VoiceIndicatorPolicy.IsSpeaking(Now - 0.31, Now));
        Assert.False(VoiceIndicatorPolicy.IsSpeaking(Now - 5, Now));
    }

    [Fact]
    public void APlayerNeverHeardIsNotSpeaking()
    {
        // Muted and out-of-range players never get a stamp, so this is also how the icon
        // stays off for them without any special case.
        Assert.False(VoiceIndicatorPolicy.IsSpeaking(0, Now));
        Assert.False(VoiceIndicatorPolicy.IsSpeaking(-1, Now));
    }

    [Fact]
    public void AStampFromTheFutureDoesNotPinTheIconOn()
        => Assert.False(VoiceIndicatorPolicy.IsSpeaking(Now + 10, Now));

    [Fact]
    public void SpeakingPrefixesTheNameWithTheIcon()
        => Assert.Equal("X HealingVision",
            VoiceIndicatorPolicy.BuildNameLabel("HealingVision", speaking: true, icon: "X"));

    [Fact]
    public void TheNameDoesNotShiftWhenSomeoneStartsTalking()
    {
        // The label is centre-aligned: a prefix that appears and disappears would slide the
        // whole name sideways on every word.
        var silent = VoiceIndicatorPolicy.BuildNameLabel("HealingVision", speaking: false, icon: "X");
        var talking = VoiceIndicatorPolicy.BuildNameLabel("HealingVision", speaking: true, icon: "X");

        Assert.Equal(talking.Length, silent.Length);
        Assert.EndsWith("HealingVision", silent, StringComparison.Ordinal);
        Assert.EndsWith("HealingVision", talking, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(silent.Substring(0, silent.Length - "HealingVision".Length)));
    }

    [Fact]
    public void TheDefaultEmojiKeepsItsWidthReserved()
    {
        // U+1F50A is a surrogate pair, so the reserved padding has to match its UTF-16 length.
        var icon = VoiceIndicatorPolicy.IconCandidates[0];
        var silent = VoiceIndicatorPolicy.BuildNameLabel("chris/", speaking: false, icon: icon);
        var talking = VoiceIndicatorPolicy.BuildNameLabel("chris/", speaking: true, icon: icon);

        Assert.Equal(talking.Length, silent.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnEmptyNameIsHandledWithoutThrowing(string name)
        => Assert.NotNull(VoiceIndicatorPolicy.BuildNameLabel(name, speaking: true, icon: "X"));

    [Fact]
    public void WithoutAnIconTheNameIsLeftAlone()
    {
        Assert.Equal("chris/", VoiceIndicatorPolicy.BuildNameLabel("chris/", speaking: true, icon: null));
        Assert.Equal("chris/", VoiceIndicatorPolicy.BuildNameLabel("chris/", speaking: false, icon: ""));
    }

    [Fact]
    public void ThePreferredIconIsUsedWhenTheFontHasIt()
        => Assert.Equal("\U0001F50A", VoiceIndicatorPolicy.ResolveIcon(_ => true));

    [Fact]
    public void AFontWithoutTheEmojiFallsBackDownTheChain()
    {
        // Game fonts rarely carry emoji; TMP would draw a blank box.
        var noteOnly = new HashSet<int> { char.ConvertToUtf32("♪", 0) };

        Assert.Equal("♪", VoiceIndicatorPolicy.ResolveIcon(noteOnly.Contains));
    }

    [Fact]
    public void AFontWithNeitherGlyphEndsOnPlainAscii()
        => Assert.Equal("*", VoiceIndicatorPolicy.ResolveIcon(_ => false));

    [Fact]
    public void TheEmojiIsProbedByCodePointNotByUtf16Unit()
    {
        var probed = new List<int>();
        VoiceIndicatorPolicy.ResolveIcon(codePoint => { probed.Add(codePoint); return false; });

        // 0x1F50A, not a lone surrogate in the 0xD800-0xDFFF range.
        Assert.Contains(0x1F50A, probed);
        Assert.DoesNotContain(probed, c => c >= 0xD800 && c <= 0xDFFF);
    }

    [Fact]
    public void AFontThatCannotAnswerDoesNotBreakTheLabel()
        => Assert.Equal("*", VoiceIndicatorPolicy.ResolveIcon(_ => throw new InvalidOperationException()));

    [Fact]
    public void NoProbeAtAllStillYieldsAUsableIcon()
        => Assert.False(string.IsNullOrEmpty(VoiceIndicatorPolicy.ResolveIcon(null)));
}
