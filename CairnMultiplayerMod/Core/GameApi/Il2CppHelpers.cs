using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

public static unsafe partial class CairnGameApi
{
    private static MonoBehaviour FindMonoBehaviourByName(string typeName, ref MonoBehaviour cache, ref int lastSearchFrame)
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
                    Mod.LogDebug($"[CairnGameApi] {typeName} found");
                    return comp;
                }
            }
        }
        catch (Exception ex)
        {
            Mod.Log.Warning($"[CairnGameApi] {typeName} search failed: {ex.Message}");
        }

        return null;
    }
}
