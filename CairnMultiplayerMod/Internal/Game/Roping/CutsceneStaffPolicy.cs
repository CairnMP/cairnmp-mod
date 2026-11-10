using System.Numerics;
using System.Text;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>What a story scene does to the staff that stands in for the leader.</summary>
internal enum CutsceneStaffChoice
{
    Ignore = 0,
    Plant,
    Remove,
    Watch,
}

/// <summary>
/// A tied leader who is taken by a scene that moves her, or by one that would end the tie,
/// leaves a stick where she was. The follower takes that stick out by starting the same scene.
/// A scene that leaves her standing still does not plant one.
/// </summary>
internal static class CutsceneStaffPolicy
{
    internal const int MaxKeyBytes = 120;

    /// <summary>
    /// How long she has to be back on the mountain before a later rest is no longer the
    /// scene that planted the stick.
    /// </summary>
    internal const float BackOnTheMountainSeconds = 2f;

    internal static string Key(string area, string scene)
    {
        if (string.IsNullOrWhiteSpace(scene)) return null;
        var name = scene.Trim();
        var place = string.IsNullOrWhiteSpace(area) ? "" : area.Trim();
        var key = place.Length == 0 ? name : place + "/" + name;
        while (Encoding.UTF8.GetByteCount(key) > MaxKeyBytes && key.Length > 0)
            key = key.Substring(0, key.Length - 1);
        return key.Length == 0 ? null : key;
    }

    /// <summary>
    /// The leader plants, or watches until her body actually leaves. The follower removes
    /// the stick for this same scene. A death is left to the usual untie.
    /// </summary>
    internal static CutsceneStaffChoice Begin(
        bool dead, bool tiedLeader, bool follower, bool endsTie, bool farMove, bool openForKey, bool hasKey)
    {
        if (!hasKey || dead) return CutsceneStaffChoice.Ignore;
        if (follower && openForKey) return CutsceneStaffChoice.Remove;
        if (!tiedLeader || openForKey) return CutsceneStaffChoice.Ignore;
        if (endsTie || farMove) return CutsceneStaffChoice.Plant;
        return CutsceneStaffChoice.Watch;
    }

    /// <summary>She left the spot the scene started from. The stick uses that earlier spot.</summary>
    internal static bool Moved(Vector3 origin, Vector3 now)
        => TopAnchorPolicy.Finite(origin) && TopAnchorPolicy.Finite(now)
           && Vector3.Distance(origin, now) > RopeAttachmentPolicy.TeleportGuardMeters;

    /// <summary>
    /// The story rest that belongs to this scene waits. A camp she chose, a pass-out,
    /// and a rest after she has been back on the mountain still clear the rope.
    /// </summary>
    internal static bool RestWaits(bool storyHold, bool campRest) => storyHold && !campRest;

    internal static bool BackOnTheMountain(float secondsInGame)
        => float.IsFinite(secondsInGame) && secondsInGame >= BackOnTheMountainSeconds;

    /// <summary>Loading drops the harness rope and leaves this stick. A death does not.</summary>
    internal static bool SafetyKeepsStick(RopeSafetyAction action)
        => action is RopeSafetyAction.Continue or RopeSafetyAction.ReleaseAnchors;
}
