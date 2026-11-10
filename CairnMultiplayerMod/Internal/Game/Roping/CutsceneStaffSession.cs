using System.Collections.Generic;
using System.Numerics;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// The scene the leader is in, and the rest that must not throw the stick away.
/// Cleared with the chain.
/// </summary>
internal static class CutsceneStaffSession
{
    private static readonly Dictionary<string, Vector3> Origins = new();
    private static bool _campRest;
    private static float _inGameSince = -1f;

    internal static bool StoryHold { get; private set; }
    internal static bool Watching { get; private set; }
    internal static string WatchKey { get; private set; }
    internal static Vector3 WatchOrigin { get; private set; }

    internal static void NoteOrigin(string key, Vector3 at)
    {
        if (string.IsNullOrEmpty(key) || !TopAnchorPolicy.Finite(at)) return;
        Origins[key] = at;
    }

    internal static bool TryOrigin(string key, out Vector3 at)
        => Origins.TryGetValue(key ?? "", out at) && TopAnchorPolicy.Finite(at);

    internal static void BeginWatch(string key, Vector3 origin)
    {
        Watching = true;
        WatchKey = key;
        WatchOrigin = origin;
    }

    internal static void EndWatch()
    {
        Watching = false;
        WatchKey = null;
        WatchOrigin = default;
    }

    /// <summary>This scene's rest must leave the stick until the follower starts it.</summary>
    internal static void ArmStory() => StoryHold = true;

    /// <summary>She chose this rest from the camp. It is not the scene that planted the stick.</summary>
    internal static void NoteCampRest() => _campRest = true;

    internal static bool TakeCampRest()
    {
        var camp = _campRest;
        _campRest = false;
        return camp;
    }

    /// <summary>
    /// Two seconds back on the mountain means the scene's own rest has passed.
    /// A later rest, including a pass-out, clears the rope.
    /// </summary>
    internal static void NoteGameplay(float now, bool inGame)
    {
        if (!StoryHold)
        {
            _inGameSince = -1f;
            return;
        }

        if (!inGame || !float.IsFinite(now))
        {
            _inGameSince = -1f;
            return;
        }

        if (_inGameSince < 0f) _inGameSince = now;
        if (CutsceneStaffPolicy.BackOnTheMountain(now - _inGameSince))
            StoryHold = false;
    }

    internal static void Reset()
    {
        Origins.Clear();
        StoryHold = false;
        Watching = false;
        WatchKey = null;
        WatchOrigin = default;
        _campRest = false;
        _inGameSince = -1f;
    }
}
