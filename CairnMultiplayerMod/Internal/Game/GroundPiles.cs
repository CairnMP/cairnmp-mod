using System;
using System.Collections.Generic;
using CairnMultiplayerMod.GameApi;
using CairnMultiplayerMod.Internal.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace CairnMultiplayerMod.Internal.Game;

/// <summary>
/// Gathers dropped items into a real in-world bag. The bag is a copy of one of the game's own
/// multi-item loot bags (a tent bag, a bear box) carrying the dropped items as its loot, so it
/// is opened and emptied like any container. The host still owns the items: taking one from the
/// bag only asks the host for it, and the item arrives through the normal pickup delivery.
/// Nothing is spawned unless the copy is confirmed not to be saved with the game.
/// </summary>
internal sealed class GroundPiles
{
    private const float PileRadius = 1.5f;
    private const float RequestTimeoutSeconds = 3f;
    private const float TemplateRetrySeconds = 5f;

    private static GroundPiles _current;
    private static bool _patched;

    private readonly List<Pile> _piles = new();
    private readonly Dictionary<uint, Pile> _pileOf = new();
    private readonly Dictionary<string, float> _templateRetry = new();
    private List<GroundItem> _desired = new();
    private Action<uint> _take;
    private bool _unsafeReported;

    private readonly Func<string, bool> _isSceneLoaded;

    internal GroundPiles(Func<string, bool> isSceneLoaded)
    {
        _isSceneLoaded = isSceneLoaded;
        _current = this;
    }

    private sealed class Pile
    {
        internal string Scene;
        internal Vector3 Center;
        internal List<GroundItem> Items = new();
        internal GameObject Object;
        internal LootProvider Provider;
        internal readonly HashSet<uint> Requested = new();
        internal float FirstRequestAt;
    }

    internal void SetTakeHandler(Action<uint> take) => _take = take;

    /// <summary>True when the item sits in a spawned bag, so it needs no floating prompt.</summary>
    internal bool IsInBag(uint groundItemId)
        => _pileOf.TryGetValue(groundItemId, out var pile) && pile.Object != null;

    internal void SetItems(IReadOnlyList<GroundItem> items)
    {
        _desired = new List<GroundItem>(items ?? Array.Empty<GroundItem>());
        Rebuild();
    }

    internal void Tick()
    {
        var rebuild = false;
        foreach (var pile in _piles)
        {
            if (pile.Object == null && pile.Provider != null) { pile.Provider = null; rebuild = true; }
            if (pile.Requested.Count > 0 && Time.unscaledTime - pile.FirstRequestAt > RequestTimeoutSeconds)
            {
                // The host did not hand the item over, so put it back in the bag.
                pile.Requested.Clear();
                DestroyPile(pile);
                rebuild = true;
            }
        }

        foreach (var pile in _piles)
            if (pile.Object == null && _isSceneLoaded(pile.Scene)) { rebuild = true; break; }
        if (rebuild) Rebuild();
    }

    private void Rebuild()
    {
        // Group the wanted items into piles, keeping existing piles whose contents are unchanged.
        var groups = new List<Pile>();
        foreach (var item in _desired)
        {
            Pile target = null;
            foreach (var group in groups)
            {
                if (group.Scene != item.Scene) continue;
                var dx = group.Center.x - item.X; var dy = group.Center.y - item.Y; var dz = group.Center.z - item.Z;
                if (dx * dx + dy * dy + dz * dz > PileRadius * PileRadius) continue;
                target = group;
                break;
            }
            if (target == null)
            {
                target = new Pile { Scene = item.Scene, Center = new Vector3(item.X, item.Y, item.Z) };
                groups.Add(target);
            }
            target.Items.Add(item);
        }

        var next = new List<Pile>();
        foreach (var group in groups)
        {
            var existing = _piles.Find(p => p.Scene == group.Scene && SameItems(p.Items, group.Items));
            if (existing != null) { _piles.Remove(existing); next.Add(existing); continue; }
            next.Add(group);
        }
        foreach (var stale in _piles) DestroyPile(stale);
        _piles.Clear();
        _piles.AddRange(next);

        _pileOf.Clear();
        foreach (var pile in _piles)
        {
            if (pile.Object == null) TrySpawn(pile);
            foreach (var item in pile.Items) _pileOf[item.Id] = pile;
        }
    }

    private static bool SameItems(List<GroundItem> left, List<GroundItem> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var item in left)
            if (!right.Exists(other => other.Id == item.Id)) return false;
        return true;
    }

    private void TrySpawn(Pile pile)
    {
        try
        {
            if (!_isSceneLoaded(pile.Scene)) return;
            if (_templateRetry.TryGetValue(pile.Scene, out var retryAt) && Time.unscaledTime < retryAt) return;
            var template = FindTemplate();
            if (template == null)
            {
                _templateRetry[pile.Scene] = Time.unscaledTime + TemplateRetrySeconds;
                return;
            }

            var rotation = Quaternion.Euler(0f, template.transform.eulerAngles.y, 0f);
            var clone = UnityEngine.Object.Instantiate(template.gameObject, pile.Center, rotation);
            clone.name = "CairnMP Ground Bag";
            var provider = clone.GetComponent<LootProvider>();
            if (provider != null && provider.IsPersistent)
            {
                // A copy inherits the original's save identity. Clear it so the bag is a
                // runtime-only object, as the game's own spawned loot is, and check again.
                var before = provider.UniquePersistentID;
                provider.UniquePersistentID = 0UL;
                ModLog.Info($"[Pile] Copy was persistent (id {before}); cleared -> persistent={provider.IsPersistent}");
            }
            if (provider == null || provider.IsPersistent)
            {
                // A saved copy would write itself into the player's save game. Never keep one.
                UnityEngine.Object.Destroy(clone);
                if (!_unsafeReported)
                    ModLog.Warning("[Pile] The loot bag copy would be saved with the game; using floating icons instead.");
                _unsafeReported = true;
                _templateRetry[pile.Scene] = Time.unscaledTime + 60f;
                return;
            }

            provider.lootTable = BuildTable(pile.Items);
            try { provider.ResetRemainingLoot(); } catch (Exception exception) { ModLog.SuppressedException("pile.reset-loot", exception); }
            pile.Object = clone;
            pile.Provider = provider;
            InstallPatch();
            ModLog.Info($"[Pile] Spawned a bag with {pile.Items.Count} item(s) from '{template.gameObject.name}'");
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[Pile] Could not spawn a bag: {exception.Message}");
            _templateRetry[pile.Scene] = Time.unscaledTime + 30f;
        }
    }

    private static LootProvider FindTemplate()
    {
        LootProvider fallback = null;
        foreach (var provider in UnityEngine.Object.FindObjectsOfType<LootProvider>(false))
        {
            if (provider == null || !provider.gameObject.activeInHierarchy
                || provider.gameObject.name == "CairnMP Ground Bag" || !provider.alwaysUseMultiLoot) continue;
            if (provider.source == LootProviderSource.Bag) return provider;
            if (provider.source == LootProviderSource.BearBox) fallback ??= provider;
        }
        return fallback;
    }

    private static LootTable BuildTable(List<GroundItem> items)
    {
        var table = ScriptableObject.CreateInstance<LootTable>();
        table.infiniteStocks = false;
        var loots = new Il2CppReferenceArray<Loot>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var loot = new Loot();
            loot.itemId = (InventoryItemStringId)items[index].DefinitionId;
            loot.partsCount = 1;
            loots[index] = loot;
        }
        table.loots = loots;
        return table;
    }

    private void DestroyPile(Pile pile)
    {
        if (pile.Object != null) UnityEngine.Object.Destroy(pile.Object);
        pile.Object = null;
        pile.Provider = null;
    }

    internal void Clear()
    {
        foreach (var pile in _piles) DestroyPile(pile);
        _piles.Clear();
        _pileOf.Clear();
        _desired.Clear();
    }

    private static void InstallPatch()
    {
        if (_patched) return;
        _patched = true;
        try
        {
            var add = AccessTools.Method(typeof(LootProvider), "AddItemInternal");
            var prefix = typeof(GroundPiles).GetMethod(nameof(AddItemPrefix),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (add == null || prefix == null) { ModLog.Warning("[Pile] Loot hook not found"); return; }
            new HarmonyLib.Harmony("CairnMultiplayerMod.GroundPiles").Patch(add, prefix: new HarmonyMethod(prefix));
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[Pile] Could not hook looting: {exception.Message}");
        }
    }

    // Taking an item out of one of our bags must not add it to the backpack here: the host owns
    // the item and delivers it. Report it as taken so the bag's own screen removes it.
    private static bool AddItemPrefix(LootProvider __instance, InventoryItemStringId __0, int __2, ref int __result)
    {
        try
        {
            var pile = _current?._piles.Find(p => p.Provider != null && p.Provider.Pointer == __instance.Pointer);
            if (pile == null) return true;

            var taken = 0;
            for (var n = 0; n < Math.Max(1, __2); n++)
            {
                var item = pile.Items.Find(i => i.DefinitionId == (int)__0.value && !pile.Requested.Contains(i.Id));
                if (item.Id == 0) break;
                if (pile.Requested.Count == 0) pile.FirstRequestAt = Time.unscaledTime;
                pile.Requested.Add(item.Id);
                _current._take?.Invoke(item.Id);
                taken++;
            }
            ModLog.Info($"[Pile] Took {taken} of item {(int)__0.value} from a ground bag");
            __result = taken;
            return false;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("pile.add-item", exception);
            return true;
        }
    }
}
