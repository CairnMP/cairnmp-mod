using System;
using System.Collections.Generic;
using System.Globalization;

namespace CairnMultiplayer.Shared;

/// <summary>
/// Where a lobby climbs. The values are Cairn's own world ids, so a destination travels on
/// the wire as the game spells it and goes back to the game without a lookup table.
/// </summary>
public enum ClimbWorld
{
    /// <summary>The story on Mount Kami, entered through the player's own save.</summary>
    Kami = 0,

    /// <summary>The Lamu climbing gym, from the On the Trail: Deep Water DLC.</summary>
    Lamu = 6,

    /// <summary>The cliffs of Haken, from the same DLC.</summary>
    Haken = 7,

    /// <summary>The cliffs of Ruka, from the same DLC.</summary>
    Ruka = 8,
}

/// <summary>The destinations CairnMP can launch a lobby into, and how to name them.</summary>
public static class ClimbWorlds
{
    /// <summary>The DLC every destination but Kami comes from. It is free, but it still has
    /// to be added to the player's Steam library.</summary>
    public const string DeepWaterDlcName = "On the Trail: Deep Water";

    private static readonly ClimbWorld[] All =
        { ClimbWorld.Kami, ClimbWorld.Lamu, ClimbWorld.Haken, ClimbWorld.Ruka };

    public static IReadOnlyList<ClimbWorld> Available => All;

    /// <summary>Kami is played from a story save; every other destination is a Summit-mode
    /// world with its own runs.</summary>
    public static bool IsStory(ClimbWorld world) => world == ClimbWorld.Kami;

    public static bool IsKnown(ClimbWorld world) => Array.IndexOf(All, world) >= 0;

    public static string Name(ClimbWorld world) => world switch
    {
        ClimbWorld.Kami => "Mount Kami",
        ClimbWorld.Lamu => "Lamu gym",
        ClimbWorld.Haken => "Haken",
        ClimbWorld.Ruka => "Ruka",
        _ => "Unknown destination",
    };

    /// <summary>What a host should know before picking a Summit world: the mode still decides
    /// what happens to the fallen there, but not the difficulty.</summary>
    public static string SummitNote(ClimbWorld world)
        => $"{Name(world)} is a Summit run: the game sets the difficulty, the mode keeps its rules.";

    /// <summary>
    /// Reads a destination off the wire. A missing value is Kami, which is where every lobby
    /// climbed before destinations existed. An unknown one is kept as it is, so the launch can
    /// refuse it instead of quietly sending everyone to Kami.
    /// </summary>
    public static ClimbWorld Parse(string? raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? (ClimbWorld)value
            : ClimbWorld.Kami;
}
