using System;

namespace CairnMultiplayerMod.Internal.Game.Voice;

/// <summary>
/// Decides when a remote player counts as speaking, and what their floating name label reads.
/// Kept free of Unity and IL2CPP types so both rules can be tested.
/// </summary>
internal static class VoiceIndicatorPolicy
{
    /// <summary>
    /// How long a player keeps counting as speaking after the last voice packet. Opus packets
    /// arrive every 20-60 ms, so this covers the gap between two of them without leaving the
    /// icon lit once someone stops talking.
    /// </summary>
    internal const double SpeakingHoldSeconds = 0.3;

    /// <summary>
    /// Candidate icons, most wanted first. U+1F50A is an emoji and game fonts rarely carry it,
    /// so the chain ends on a character every font has.
    /// </summary>
    internal static readonly string[] IconCandidates = { "\U0001F50A", "♪", "*" };

    internal static bool IsSpeaking(double lastReceived, double now)
    {
        // A player we have never heard from has no stamp at all.
        if (lastReceived <= 0) return false;

        var age = now - lastReceived;
        // A stamp from the future means the clock was reset under us; treat it as silence
        // rather than pinning the icon on forever.
        if (age < 0) return false;

        return age < SpeakingHoldSeconds;
    }

    /// <summary>
    /// The floating label. The prefix keeps its width whether or not the player is speaking:
    /// the label is centre-aligned, so a prefix that appears and disappears would shift the
    /// whole name sideways every time someone talks.
    /// </summary>
    internal static string BuildNameLabel(string playerName, bool speaking, string icon)
    {
        var name = playerName ?? string.Empty;
        if (string.IsNullOrEmpty(icon)) return name;

        return speaking
            ? icon + " " + name
            : new string(' ', icon.Length + 1) + name;
    }

    /// <summary>
    /// First candidate the font can actually draw. <paramref name="canRender"/> receives a
    /// Unicode code point, not a char: the preferred icon sits outside the BMP, so testing a
    /// single UTF-16 unit would probe a lone surrogate.
    /// </summary>
    internal static string ResolveIcon(Func<int, bool> canRender)
    {
        if (canRender == null) return IconCandidates[^1];

        foreach (var candidate in IconCandidates)
        {
            if (candidate.Length == 0) continue;

            var codePoint = char.ConvertToUtf32(candidate, 0);
            try
            {
                if (canRender(codePoint)) return candidate;
            }
            catch (Exception)
            {
                // A font that cannot answer is a font we should not bet the label on.
                break;
            }
        }

        return IconCandidates[^1];
    }
}
