using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayerMod.Internal.Networking;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using Vector3 = System.Numerics.Vector3;

namespace CairnMultiplayerMod.Internal.Game.Roping;

internal static partial class RopeInterop
{
    /// <summary>Where she was on the last frame before this scene. The scene itself may already have moved her.</summary>
    private static Vector3 _cutsceneBefore;
    private static bool _hasCutsceneBefore;
    private static IntPtr _cutsceneBegun;
    private static string _cutsceneKey;

    /// <summary>
    /// Runs every frame, including while a story scene has the climber marked as loading.
    /// The scene is read from the one already playing. Play and the static cutscene events
    /// are left alone: hooking either one overflows the stack before the menu appears.
    /// </summary>
    internal static void TickCutsceneStaff(NetworkManager network, bool inGame, float now)
    {
        if (network != null) RememberTopAnchorNetwork(network);
        CutsceneStaffSession.NoteGameplay(now, inGame);
        // Only while Cairn itself says this is a story scene. The menu is left alone,
        // and Play is not patched.
        var story = StorySceneIsRunning();
        Cutscene scene = null;
        var playing = story && TryCurrentCutscene(out scene);
        if (playing && scene.Pointer != _cutsceneBegun)
            BeginCurrentCutscene(scene);

        if (CutsceneStaffSession.Watching
            && TryGetClimberPosition(out var moved)
            && CutsceneStaffPolicy.Moved(CutsceneStaffSession.WatchOrigin, moved))
        {
            var watched = CutsceneStaffSession.WatchKey;
            var from = CutsceneStaffSession.WatchOrigin;
            CutsceneStaffSession.EndWatch();
            PlaceCutsceneStaff(watched, from, endsTie: false);
        }

        if (story) return;

        if (_cutsceneBegun != IntPtr.Zero)
            EndCurrentCutscene();
        if (TryGetClimberPosition(out var body) && TopAnchorPolicy.Finite(body))
        {
            _cutsceneBefore = body;
            _hasCutsceneBefore = true;
        }
    }

    private static bool StorySceneIsRunning()
        => GameLifecycleService.TryGetRawGameState(out var state)
           && state == CairnGameLifecycleState.Cutscene;

    private static bool TryCurrentCutscene(out Cutscene scene)
    {
        scene = null;
        try
        {
            scene = Cutscene.CurrentCutscene;
            if (scene == null) return false;
            return scene.IsPlaying || Cutscene.CurrentCutsceneIsPlaying;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.cutscene-current", exception);
            scene = null;
            return false;
        }
    }

    private static void BeginCurrentCutscene(Cutscene scene)
    {
        try
        {
            _cutsceneBegun = scene.Pointer;
            var key = SceneKey(scene);
            _cutsceneKey = key;
            if (key != null && _hasCutsceneBefore)
                CutsceneStaffSession.NoteOrigin(key, _cutsceneBefore);
            BeginCutscene(scene);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.cutscene-play", exception);
        }
    }

    private static void EndCurrentCutscene()
    {
        var key = _cutsceneKey;
        _cutsceneBegun = IntPtr.Zero;
        _cutsceneKey = null;
        if (CutsceneStaffSession.Watching && CutsceneStaffSession.WatchKey == key)
            CutsceneStaffSession.EndWatch();
    }

    private static void BeginCutscene(Cutscene scene)
    {
        var key = SceneKey(scene);
        if (key == null) return;
        var dead = PawnCaptureInterop.GetLocalPawnState() == NetFrame.PawnStateType.Dead;
        var leader = TeamAnchorSession.IsLocalLeader && !TeamAnchorSession.LeaderLeft;
        var follower = TeamAnchorSession.IsLocalFollower;
        var open = TopAnchorSession.HasKey(key);
        if (!CutsceneStaffSession.TryOrigin(key, out var origin) && !TryGetClimberPosition(out origin))
            origin = default;
        var endsTie = EndsTheTie(scene);
        var far = MarkedFar(scene, origin);
        var choice = CutsceneStaffPolicy.Begin(dead, leader, follower, endsTie, far, open, hasKey: true);
        switch (choice)
        {
            case CutsceneStaffChoice.Remove:
                CutsceneStaffSession.EndWatch();
                RemoveCutsceneStick(key);
                break;
            case CutsceneStaffChoice.Plant:
                CutsceneStaffSession.EndWatch();
                PlaceCutsceneStaff(key, origin, endsTie);
                break;
            case CutsceneStaffChoice.Watch:
                if (TopAnchorPolicy.Finite(origin))
                    CutsceneStaffSession.BeginWatch(key, origin);
                break;
        }
    }

    private static void PlaceCutsceneStaff(string key, Vector3 origin, bool endsTie)
    {
        if (string.IsNullOrEmpty(key) || !TopAnchorPolicy.Finite(origin)) return;
        var at = origin;
        var outward = new Vector3(0f, 1f, 0f);
        var unity = new UnityEngine.Vector3(origin.X, origin.Y, origin.Z);
        if (TryRockSurface(unity, out var hit, out var normal))
        {
            at = new Vector3(hit.x, hit.y, hit.z);
            outward = new Vector3(normal.x, normal.y, normal.z);
        }

        PlantCutsceneStick(at, outward, key, endsTie, _topAnchorNetwork);
    }

    private static bool EndsTheTie(Cutscene scene)
    {
        if (scene == null) return false;
        if (scene.isBivouacCutscene) return true;
        var sync = scene.syncCutsceneToBivouac;
        return sync != null && sync.syncBivouacPosition;
    }

    private static bool MarkedFar(Cutscene scene, Vector3 origin)
    {
        if (scene == null) return false;
        return FarPoint(scene.aavaTeleportPointAtStart, origin)
               || FarPoint(scene.aavaTeleportPointAtEnd, origin);
    }

    private static bool FarPoint(Transform point, Vector3 origin)
    {
        if (point == null) return false;
        var at = point.position;
        return CutsceneStaffPolicy.Moved(origin, new Vector3(at.x, at.y, at.z));
    }

    private static string SceneKey(Cutscene scene)
    {
        if (scene == null) return null;
        string area = null;
        try
        {
            var body = scene.gameObject;
            if (body != null) area = body.scene.name;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.cutscene-name", exception);
        }

        return CutsceneStaffPolicy.Key(area, scene.name);
    }
}
