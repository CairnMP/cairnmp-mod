using System;
using System.Collections.Generic;

namespace CairnMultiplayerMod.GameApi;

/// <summary>A conservative, serializable view of an item that may be shared.</summary>
internal readonly struct ShareableItem
{
    public ShareableItem(ushort uniqueId, int definitionId, string name, int count)
    {
        UniqueId = uniqueId;
        DefinitionId = definitionId;
        Name = name ?? "";
        Count = count;
    }

    public ushort UniqueId { get; }
    public int DefinitionId { get; }
    public string Name { get; }
    public int Count { get; }
}

/// <summary>A host-owned item currently available to pick up in the world.</summary>
internal readonly struct GroundItem
{
    public GroundItem(uint id, int definitionId, string name, float x, float y, float z, string scene)
    {
        Id = id; DefinitionId = definitionId; Name = name ?? "";
        X = x; Y = y; Z = z; Scene = scene ?? "";
    }

    public uint Id { get; }
    public int DefinitionId { get; }
    public string Name { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public string Scene { get; }
}

/// <summary>
/// Safe inventory operations used by item sharing. The implementation deliberately exposes
/// only stateless consumables: progression items and gear with per-unit data never cross the
/// network through this API.
/// </summary>
internal interface IInventoryApi
{
    bool TryGetSelectedShareableItem(out ShareableItem item);
    IGameRegistration AddShareActions(Func<bool> canGive, Action<ShareableItem> give,
        Func<bool> canDrop, Action<ShareableItem> drop);
    void SetGroundItems(IReadOnlyList<GroundItem> items);
    void DrawGroundItems();
    uint SelectGroundItem(IReadOnlyList<GroundItem> nearbyItems);
    bool IsShareableDefinition(int definitionId);
    bool CanAccept(int definitionId, int count, out string reason);
    bool TryRemove(ushort uniqueId, int definitionId, int count, out string reason);
    bool TryRemoveAny(int definitionId, int count, out string reason);
    bool TryAdd(int definitionId, int count, out string reason);
}
