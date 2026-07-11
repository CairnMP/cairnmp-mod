using System;
using Il2Cpp;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Acces au point d'attache de la corde sur le baudrier (Harness). Le jeu expose
/// `Harness.GetAttachPosition()` : la position monde exacte ou la corde se noue au
/// baudrier. On l'utilise pour ancrer la corde entre joueurs comme le fait Cairn,
/// au lieu d'un offset vertical approximatif sur la racine du corps.
/// </summary>
public static unsafe partial class CairnGameApi
{
    private static Harness _localHarnessCached;
    private static int _lastLocalHarnessSearchFrame;

    /// <summary>
    /// Position monde du point d'attache du baudrier du joueur local, ou false si le
    /// baudrier n'est pas (encore) disponible.
    /// </summary>
    public static bool TryGetLocalHarnessAttachPosition(out Vector3 pos)
    {
        pos = default;
        var harness = ResolveLocalHarness();
        return harness != null && TryGetHarnessAttachPosition(harness, out pos);
    }

    /// <summary>Renvoie le composant Harness du joueur local (pour la sonde belay / corde physique).</summary>
    public static bool TryGetLocalHarness(out Harness harness)
    {
        harness = ResolveLocalHarness();
        return harness != null;
    }

    /// <summary>
    /// Position monde du point d'attache d'un baudrier quelconque (local ou fantome).
    /// Replie sur `skeletonAttachPointRoot` si GetAttachPosition renvoie l'origine
    /// (baudrier pas encore initialise / physique inactive cote fantome).
    /// </summary>
    public static bool TryGetHarnessAttachPosition(Harness harness, out Vector3 pos)
    {
        pos = default;
        if (harness == null) return false;
        try
        {
            pos = harness.GetAttachPosition();
            if (pos != Vector3.zero) return true;

            // Repli : la racine de l'attache squelette suit la pose meme sans physique.
            var root = harness.skeletonAttachPointRoot;
            if (root != null)
            {
                pos = root.position;
                return pos != Vector3.zero;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static Harness ResolveLocalHarness()
    {
        if (_localHarnessCached != null) return _localHarnessCached;

        // Recherche throttlee tant qu'on n'a rien trouve (le MC peut apparaitre tard).
        if (_lastLocalHarnessSearchFrame != 0 && Time.frameCount - _lastLocalHarnessSearchFrame < 30)
            return null;
        _lastLocalHarnessSearchFrame = Time.frameCount;

        var mc = TryGetLocalMCGameObject();
        if (mc == null) return null;

        // Le baudrier local est un Harness de la hierarchie du MC — mais PAS un
        // NetplayRemoteHarness (ceux-la appartiennent aux fantomes).
        var harnesses = mc.GetComponentsInChildren<Harness>(true);
        if (harnesses == null) return null;
        for (int i = 0; i < harnesses.Length; i++)
        {
            var h = harnesses[i];
            if (h == null) continue;
            if (h.TryCast<Il2CppTheGameBakers.Cairn.Netplay.NetplayRemoteHarness>() != null) continue;
            _localHarnessCached = h;
            Mod.LogDebug("[Harness] Local harness resolved on MC hierarchy");
            return h;
        }
        return null;
    }
}
