using System;

namespace CairnMultiplayerMod.Internal.Game.Roping;

internal static class RopeAttachmentPolicy
{
    internal static bool CanReach(float maxLength, float distance)
        => float.IsFinite(maxLength) && float.IsFinite(distance)
           && maxLength > 0 && distance >= 0 && distance < maxLength;
}
