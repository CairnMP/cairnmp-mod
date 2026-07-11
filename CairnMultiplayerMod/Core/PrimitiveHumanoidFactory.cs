using UnityEngine;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Builds a primitive humanoid (capsule for the body + sphere for the head + arms/legs)
/// as a fallback visual for ghosts when the real Cairn model isn't available.
/// </summary>
public static class PrimitiveHumanoidFactory
{
    public static GameObject Build(int id, string name, Color color)
    {
        var root = new GameObject($"MP_Ghost_{id}_{name}");
        var c = color;

        Part("Torso",     PrimitiveType.Capsule, root, new(0f,    1.0f, 0f), new(0.55f, 0.55f, 0.55f), c);
        Part("Head",      PrimitiveType.Sphere,  root, new(0f,    1.7f, 0f), new(0.35f, 0.35f, 0.35f), c);
        Part("ArmL",      PrimitiveType.Capsule, root, new(-0.35f,1.0f, 0f), new(0.18f, 0.45f, 0.18f), c);
        Part("ArmR",      PrimitiveType.Capsule, root, new(0.35f, 1.0f, 0f), new(0.18f, 0.45f, 0.18f), c);
        Part("LegL",      PrimitiveType.Capsule, root, new(-0.18f,0.4f, 0f), new(0.22f, 0.50f, 0.22f), c);
        Part("LegR",      PrimitiveType.Capsule, root, new(0.18f, 0.4f, 0f), new(0.22f, 0.50f, 0.22f), c);
        Part("NamePlate", PrimitiveType.Cube,    root, new(0f,    2.25f,0f), new(0.50f, 0.15f, 0.15f), Color.white);

        return root;
    }

    private static void Part(string n, PrimitiveType t, GameObject parent,
        Vector3 pos, Vector3 scale, Color color)
    {
        var go = GameObject.CreatePrimitive(t);
        go.name = n;
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null) mr.material.color = color;
        var col = go.GetComponent<Collider>();
        if (col != null) col.enabled = false;
    }
}
