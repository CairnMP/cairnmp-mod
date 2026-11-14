#nullable enable

namespace CairnMultiplayerMod.Internal.Networking.Authoritative;

/// <summary>
/// A new tie while a native Cairn rope still holds a piton breaks that rope and
/// leaves the piton in the rock. The piton can then join the shared chain, and an
/// unclip snaps a climber to it. The tie waits until the piton is recalled.
/// Each climber answers for their own rope. Unclip and rejoining a held pair stay allowed.
/// </summary>
internal static class NativeRopeClipPolicy
{
    /// <summary>Shown on the clip-in row, and only while a partner is in reach.</summary>
    internal const string RecallPrompt = "Recall piton first";

    internal static bool BlocksNewTie(
        bool clip, bool resumesHeldPair, bool selfHoldsNativePiton, bool partnerHoldsNativePiton)
        => clip && !resumesHeldPair && (selfHoldsNativePiton || partnerHoldsNativePiton);

    /// <summary>
    /// The same row as clip-in. It stays hidden while already tied, while nobody is
    /// in reach, and while the native rope is clear, so a solo climb is not nagged.
    /// </summary>
    internal static bool ShouldPromptRecall(
        bool alreadyLinked, bool hasTargetInRange, bool inputAvailable, bool selfHoldsNativePiton)
        => !alreadyLinked && hasTargetInRange && inputAvailable && selfHoldsNativePiton;

    /// <summary>A piton already in the rock when the tie started is not a chain anchor.</summary>
    internal static bool MayJoinChain(bool sealedAsNative) => !sealedAsNative;

    /// <summary>
    /// Unclip may detach, and Cairn may snap to, only a piton this tie put on the chain.
    /// </summary>
    internal static bool MaySnapOnUnclip(bool onThisTieChain, bool sealedAsNative)
        => onThisTieChain && !sealedAsNative;
}
