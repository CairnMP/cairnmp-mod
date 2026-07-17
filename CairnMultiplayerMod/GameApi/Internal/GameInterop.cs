using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnMultiplayerMod.GameApi.Internal;

/// <summary>
/// Low-level IL2CPP interop primitives shared by every game-facing service. These have
/// no feature state of their own: callers pass in their own caches by reference, so the
/// helper stays a pure lookup that any service can reuse without coupling them together.
/// </summary>
internal static class GameInterop
{
    /// <summary>
    /// Finds the first active MonoBehaviour whose IL2CPP type name matches, caching the
    /// result in the caller's own fields and rate-limiting the (expensive) scan to once
    /// every 60 frames. Returns null until found; safe on any exception.
    /// </summary>
    internal static MonoBehaviour FindMonoBehaviourByName(string typeName, ref MonoBehaviour cache, ref int lastSearchFrame)
    {
        if (cache != null) return cache;

        int frame = Time.frameCount;
        if (frame - lastSearchFrame < 60) return null;
        lastSearchFrame = frame;

        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<MonoBehaviour>());
            if (all == null) return null;

            for (int i = 0; i < all.Count; i++)
            {
                var comp = all[i].TryCast<MonoBehaviour>();
                if (comp == null) continue;
                if (comp.GetIl2CppType().Name == typeName)
                {
                    cache = comp;
                    Mod.LogDebug($"[GameInterop] {typeName} found");
                    return comp;
                }
            }
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[GameInterop] {typeName} search failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>Returns the first line of a (possibly multi-line) string — used to keep
    /// exception messages to a single readable line in the logs.</summary>
    internal static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var index = text.IndexOfAny(new[] { '\r', '\n' });
        return index >= 0 ? text.Substring(0, index) : text;
    }
}
