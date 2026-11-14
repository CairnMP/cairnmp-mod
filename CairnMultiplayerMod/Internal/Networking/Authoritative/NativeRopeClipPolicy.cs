#nullable enable

using System;

namespace CairnMultiplayerMod.Internal.Networking.Authoritative;

/// <summary>
/// A new tie while a native Cairn rope still holds a piton breaks that rope and
/// leaves the piton in the rock. The piton can then join the shared chain, and an
/// unclip snaps a climber to it. The tie waits until the piton is recalled.
/// Each climber answers for their own rope. Unclip and rejoining a held pair stay allowed.
/// </summary>
internal static class NativeRopeClipPolicy
{
    /// <summary>
    /// Shown on the clip-in row while this climber's own rope still holds a piton.
    /// Cairn's call is <c>RecallClimbot</c>. The on-screen loc string is not in this repo.
    /// </summary>
    internal const string RecallPrompt = "Recall Climbot to tie in";

    /// <summary>How long a tie refusal stays up after they walk out of reach.</summary>
    internal const float TieRefusalSeconds = 2.5f;

    /// <summary>
    /// The recall gate is for a new tie. Unclip stays open. A rejoin of a held
    /// pair skips this gate, so the native-piton read does not matter there.
    /// </summary>
    internal static bool RecallGateApplies(bool clip, bool resumesHeldPair)
        => clip && !resumesHeldPair;

    internal static string PartnerRecallLine(string partnerName)
    {
        var name = DisplayName(partnerName);
        return name + " needs to recall Climbot first";
    }

    internal static string NotReadyLine(string partnerName)
        => DisplayName(partnerName) + " isn't ready. Try again.";

    private static string DisplayName(string partnerName)
        => string.IsNullOrWhiteSpace(partnerName) ? "Your partner" : partnerName.Trim();

    /// <summary>
    /// The same row as clip-in. It stays hidden while already tied, while nobody is
    /// in reach, and while the native rope is clear, so a solo climb is not nagged.
    /// </summary>
    internal static bool ShouldPromptRecall(
        bool alreadyLinked, bool hasTargetInRange, bool inputAvailable, bool selfHoldsNativePiton)
        => !alreadyLinked && hasTargetInRange && inputAvailable && selfHoldsNativePiton;
}
