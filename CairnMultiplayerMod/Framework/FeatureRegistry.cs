using System.Collections.Generic;

namespace CairnMultiplayerMod.Framework;

/// <summary>
/// Every feature the mod ships. Temporary hand-written form — a source generator will emit
/// this list from the classes deriving from <see cref="MultiplayerFeature"/>, so that adding
/// a feature means adding one file and nothing else.
/// </summary>
internal static class FeatureRegistry
{
    internal static IEnumerable<MultiplayerFeature> CreateAll() => new MultiplayerFeature[]
    {
    };
}
