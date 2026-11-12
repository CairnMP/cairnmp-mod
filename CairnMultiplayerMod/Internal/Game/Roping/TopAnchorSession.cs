using System;
using System.Collections.Generic;
using System.Numerics;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// The staff pitons on this link. They share the team chain's ids and positions, and they
/// are not gear: a clean forgets the id so a late place cannot bring it back.
/// </summary>
internal static class TopAnchorSession
{
    private static readonly Dictionary<uint, Vector3> Sticks = new();
    private static readonly Dictionary<uint, Vector3> Outwards = new();
    private static readonly Dictionary<uint, string> Scenes = new();
    private static readonly Dictionary<uint, IntPtr> Pointers = new();
    private static readonly HashSet<uint> Cleaned = new();
    private static readonly List<uint> Order = new();
    private static readonly List<IntPtr> PendingDestroy = new();
    private static uint _next = TopAnchorPolicy.FirstId;

    internal static bool HasStick => Sticks.Count > 0;

    /// <summary>A stick left by a story scene. Standing beside it does not pull it.</summary>
    internal static bool HasCutsceneStick => Scenes.Count > 0;
    internal static bool HoldRequested { get; private set; }
    internal static bool DropRequested { get; private set; }

    internal static void RequestHold() => HoldRequested = true;

    /// <summary>The leave has been applied, or the same pair tied again. The next unclip is a new decision.</summary>
    internal static void ConsumeHold() => HoldRequested = false;

    internal static void RequestDrop() => DropRequested = true;

    internal static uint Allocate()
    {
        var id = _next;
        if (_next != uint.MaxValue) _next++;
        return id;
    }

    internal static bool WasCleaned(uint id) => Cleaned.Contains(id);

    internal static void Remember(uint id, Vector3 position)
    {
        if (!TopAnchorPolicy.IsStickId(id) || Cleaned.Contains(id) || !TopAnchorPolicy.Finite(position)) return;
        Sticks[id] = position;
        if (!Order.Contains(id)) Order.Add(id);
    }

    /// <summary>The story scene that left this stick, and the direction it points out of the rock.</summary>
    internal static void RememberCutscene(uint id, Vector3 outward, string key)
    {
        if (!Sticks.ContainsKey(id) || string.IsNullOrEmpty(key)) return;
        Outwards[id] = TopAnchorPolicy.OutwardOrUp(outward);
        Scenes[id] = key;
    }

    internal static bool IsCutscene(uint id) => Scenes.ContainsKey(id);

    internal static bool HasKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        foreach (var scene in Scenes.Values)
            if (scene == key) return true;
        return false;
    }

    internal static bool TryIdForKey(string key, out uint id)
    {
        id = 0;
        if (string.IsNullOrEmpty(key)) return false;
        foreach (var pair in Scenes)
        {
            if (pair.Value != key) continue;
            id = pair.Key;
            return true;
        }

        return false;
    }

    internal static Vector3 OutwardOrUp(uint id)
        => Outwards.TryGetValue(id, out var outward) ? TopAnchorPolicy.OutwardOrUp(outward) : new Vector3(0f, 1f, 0f);

    /// <summary>The nearest mantle stick. A story stick stays until that scene plays for the follower.</summary>
    internal static bool TryNearestMantle(Vector3 climber, out uint id)
        => TopAnchorPolicy.TryNearest(Order, Sticks, climber, out id, IsCutscene);

    internal static void Forget(uint id)
    {
        Drop(id);
        if (id != 0) Cleaned.Add(id);
    }

    /// <summary>A spawn that failed before the other climber was told. The id may be placed again.</summary>
    internal static void Discard(uint id) => Drop(id);

    internal static void SetPointer(uint id, IntPtr pointer)
    {
        if (pointer != IntPtr.Zero && Sticks.ContainsKey(id)) Pointers[id] = pointer;
    }

    internal static bool TryGetPointer(uint id, out IntPtr pointer)
        => Pointers.TryGetValue(id, out pointer) && pointer != IntPtr.Zero;

    internal static bool TryGetPosition(uint id, out Vector3 position)
        => Sticks.TryGetValue(id, out position);

    internal static bool OwnsPointer(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return false;
        foreach (var owned in Pointers.Values)
            if (owned == pointer) return true;
        return false;
    }

    internal static bool TryNearest(Vector3 climber, out uint id)
        => TopAnchorPolicy.TryNearest(Order, Sticks, climber, out id);

    internal static void CopyIds(List<uint> into)
    {
        into.Clear();
        into.AddRange(Order);
    }

    /// <summary>Live staff pitons. Appends. Does not clear <paramref name="into"/>.</summary>
    internal static void CopyPointers(List<IntPtr> into)
    {
        if (into == null) return;
        foreach (var pointer in Pointers.Values)
            if (pointer != IntPtr.Zero && !into.Contains(pointer)) into.Add(pointer);
    }

    internal static void DrainDestroy(List<IntPtr> into)
    {
        into.Clear();
        into.AddRange(PendingDestroy);
        PendingDestroy.Clear();
    }

    internal static void Reset()
    {
        foreach (var pointer in Pointers.Values) QueueDestroy(pointer);
        Sticks.Clear();
        Outwards.Clear();
        Scenes.Clear();
        Pointers.Clear();
        Cleaned.Clear();
        Order.Clear();
        _next = TopAnchorPolicy.FirstId;
        HoldRequested = false;
        DropRequested = false;
        CutsceneStaffSession.Reset();
    }

    private static void Drop(uint id)
    {
        if (Pointers.TryGetValue(id, out var pointer)) QueueDestroy(pointer);
        Pointers.Remove(id);
        Sticks.Remove(id);
        Outwards.Remove(id);
        Scenes.Remove(id);
        Order.Remove(id);
    }

    private static void QueueDestroy(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero && !PendingDestroy.Contains(pointer)) PendingDestroy.Add(pointer);
    }
}
