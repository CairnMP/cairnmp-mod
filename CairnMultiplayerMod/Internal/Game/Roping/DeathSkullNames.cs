using System;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>Cairn's life-bar death pictogram, recognized by the skull in its name.</summary>
internal static class DeathSkullNames
{
    internal static bool IsSkull(string name)
        => !string.IsNullOrEmpty(name)
           && name.IndexOf("Skull", StringComparison.OrdinalIgnoreCase) >= 0;
}
