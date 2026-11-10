using System;
using System.Collections.Generic;
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
    private static ushort _topAnchorChannel;
    private static TopAnchorMantle _mantle;
    private static readonly List<IntPtr> _staffDestroy = new();
    private static readonly List<uint> _staffIds = new();
    private static Vector3 _lastToppedSent;
    private static bool _toppedSent;
    private static LineRenderer _heldLine;
    private static bool _heldLineStyled;
    private static bool _heldLineLogged;
    private const int StaffPitonHp = 100;
    private const byte StaffPitonQuality = 1;

    internal static void BindTopAnchorChannel(ushort channel) => _topAnchorChannel = channel;

    internal static void SendTopAnchorLeave(NetworkManager network)
    {
        TopAnchorSession.CopyIds(_staffIds);
        SendTopAnchor(network, TopAnchorMessage.Leave(_staffIds));
    }

    internal static void SendTopAnchorDrop(NetworkManager network)
        => SendTopAnchor(network, TopAnchorMessage.Drop());

    internal static void AcceptTopAnchor(int fromPlayerId, byte[] payload)
    {
        if (!TopAnchorMessage.TryRead(payload, out var packet)) return;
        switch (packet.Fact)
        {
            case TopAnchorFact.Place:
                if (fromPlayerId != TeamAnchorSession.LeaderId || TeamAnchorSession.IsLocalLeader) return;
                AcceptPlace(packet.Id, packet.Position);
                break;
            case TopAnchorFact.Cutscene:
                if (fromPlayerId != TeamAnchorSession.LeaderId || TeamAnchorSession.IsLocalLeader) return;
                AcceptCutscene(packet.Id, packet.Position, packet.Outward, packet.Key);
                break;
            case TopAnchorFact.Clean:
                if (!PairMember(fromPlayerId)) return;
                RemoveStick(packet.Id, tellPartner: false);
                break;
            case TopAnchorFact.Topped:
                if (fromPlayerId != TeamAnchorSession.FollowerId || !TeamAnchorSession.IsLocalLeader) return;
                if (!TopAnchorSession.TryNearestMantle(packet.Position, out var stick)) return;
                RemoveStick(stick, tellPartner: true);
                break;
            case TopAnchorFact.Leave:
                // A story leave can arrive before its stick. Wait for the plant, which
                // ends the pair once the stick is on the chain. A mantle stick is already in.
                if (fromPlayerId != TeamAnchorSession.LeaderId || !TopAnchorSession.HasStick) return;
                TopAnchorSession.RequestHold();
                TeamAnchorSession.EndPartnership();
                break;
            case TopAnchorFact.Drop:
                if (fromPlayerId != TeamAnchorSession.LeaderId && fromPlayerId != TeamAnchorSession.LocalPlayerId) return;
                TopAnchorSession.RequestDrop();
                TeamAnchorSession.Clear();
                break;
        }
    }

    internal static UnityEngine.Vector3 ChainDrawPoint(uint id, Vector3 stored)
    {
        if (TopAnchorPolicy.IsStickId(id))
        {
            var tie = TopAnchorPolicy.TiePoint(stored, TopAnchorSession.OutwardOrUp(id));
            return new UnityEngine.Vector3(tie.X, tie.Y, tie.Z);
        }

        if (TryGetQuickdrawCarabiner(id, out var carabiner)) return carabiner;
        return new UnityEngine.Vector3(stored.X, stored.Y, stored.Z);
    }

    private static void TickTopAnchor(NetworkManager network)
    {
        RememberTopAnchorNetwork(network);
        DrainStaffPitons();
        TopAnchorPlant.DropMissing();
        PresentStaffPitons();
        if (!TeamAnchorSession.IsLinked)
        {
            _mantle = default;
            if (TeamAnchorSession.HasLeftoverCatch)
            {
                if (TeamAnchorSession.LocalKeepsCatch)
                    WatchFollowerStanding(network);
                DrawHeldLine(network, retired: false);
            }
            else if (TeamAnchorSession.HasRetiredCatch)
            {
                if (TeamAnchorSession.LocalKeepsCatch)
                    WatchFollowerStanding(network);
                DrawHeldLine(network, retired: true);
            }
            else
                HideHeldLine();
            return;
        }

        if (TeamAnchorSession.IsLocalLeader && !TeamAnchorSession.LeaderLeft)
            WatchLeaderMantle(network);
        if (TeamAnchorSession.IsLocalFollower || TeamAnchorSession.LocalKeepsCatch)
            WatchFollowerStanding(network);
        // The partner rope goes with the leader. This line is the follower's:
        // their harness, then every piton still on the chain, ending at the staff.
        if (TeamAnchorSession.LeaderLeft)
            DrawHeldLine(network, retired: false);
        else if (TeamAnchorSession.HasRetiredCatch)
            DrawHeldLine(network, retired: true);
        else
            HideHeldLine();
    }

    private static void WatchLeaderMantle(NetworkManager network)
    {
        var module = ReadClimbModule();
        var walking = PawnCaptureInterop.IsLocalPlayerWalking();
        if (!TryGetClimberPosition(out var harness)) harness = default;
        if (!_mantle.Observe(module, walking, harness, out var lip)) return;

        if (TopAnchorSession.TryNearestMantle(lip, out _))
        {
            ModLog.Info("[RopeTeam] Mantle finished on a ledge that already has a stick.");
            return;
        }

        if (FollowerIsStandingAt(network, lip))
        {
            ModLog.Info("[RopeTeam] Mantle finished where the follower is already standing. No stick was planted.");
            return;
        }

        lip = LedgeUnder(lip);
        var id = TopAnchorSession.Allocate();
        if (!PlantStick(id, lip, tellPartner: true, network)) return;
        ModLog.Info($"[RopeTeam] Planted a staff anchor #{id} at ({lip.X:F1},{lip.Y:F1},{lip.Z:F1}).");
    }

    private static void WatchFollowerStanding(NetworkManager network)
    {
        if (!PawnCaptureInterop.IsLocalPlayerWalking() || !TryGetClimberPosition(out var position)) return;
        if (TopAnchorSession.TryNearestMantle(position, out var stick))
        {
            _toppedSent = false;
            RemoveStick(stick, tellPartner: true);
            ModLog.Info($"[RopeTeam] The follower is standing. Staff anchor #{stick} came out.");
            return;
        }

        if (_toppedSent && Vector3.Distance(position, _lastToppedSent) < 4f) return;
        _lastToppedSent = position;
        _toppedSent = true;
        SendTopAnchor(network, TopAnchorMessage.Topped(position));
    }

    private static bool PlantStick(uint id, Vector3 lip, bool tellPartner, NetworkManager network)
        => PlantStick(id, lip, tellPartner, network, alongRock: false, default);

    private static bool PlantStick(
        uint id, Vector3 lip, bool tellPartner, NetworkManager network, bool alongRock, Vector3 outward)
    {
        var before = TeamAnchorSession.Count;
        TopAnchorSession.Remember(id, lip);
        if (TeamAnchorSession.IsLocalLeader)
            TeamAnchorSession.NoteLocalLeaderPlacement(id, lip);
        else
            TeamAnchorSession.NoteRemotePlacement(TeamAnchorSession.LeaderId, id, lip);
        var noted = TeamAnchorSession.Count > before;
        if (!noted || !SpawnStaffPiton(id, lip))
        {
            TopAnchorSession.Discard(id);
            if (noted) TeamAnchorSession.NoteRemoved(id);
            return false;
        }

        var at = new UnityEngine.Vector3(lip.X, lip.Y, lip.Z);
        if (alongRock)
            TopAnchorPlant.Plant(id, at, new UnityEngine.Vector3(outward.X, outward.Y, outward.Z));
        else
            TopAnchorPlant.Plant(id, at);
        if (tellPartner) SendTopAnchor(network, TopAnchorMessage.Place(id, lip));
        return true;
    }

    private static void AcceptPlace(uint id, Vector3 lip)
    {
        if (TopAnchorSession.WasCleaned(id) || TopAnchorSession.TryGetPosition(id, out _)) return;
        if (!PlantStick(id, lip, tellPartner: false, network: null)) return;
        ModLog.Info($"[RopeTeam] Staff anchor #{id} planted for the leader.");
    }

    private static void RemoveStick(uint id, bool tellPartner)
    {
        if (!TopAnchorSession.TryGetPosition(id, out _)) return;
        var cutscene = TopAnchorSession.IsCutscene(id);
        TopAnchorPlant.Restore(id);
        TopAnchorSession.Forget(id);
        TeamAnchorSession.NoteRemoved(id);
        if (tellPartner) SendTopAnchor(null, TopAnchorMessage.Clean(id));
        // The scene is taking the follower. This is not an untie, so nobody is dropped.
        if (cutscene) TeamAnchorSession.RejoinIfStillTied();
    }

    /// <summary>
    /// Leaves a stick where the leader was. The chain stays for the follower, and the
    /// harness rope is not dragged to wherever the scene takes her.
    /// </summary>
    internal static bool PlantCutsceneStick(Vector3 where, Vector3 outward, string key, bool endsTie, NetworkManager network)
    {
        if (string.IsNullOrEmpty(key) || TopAnchorSession.HasKey(key)) return false;
        if (!TeamAnchorSession.IsLocalLeader || TeamAnchorSession.LeaderLeft) return false;
        if (!TopAnchorPolicy.Finite(where)) return false;
        outward = TopAnchorPolicy.OutwardOrUp(outward);
        var id = TopAnchorSession.Allocate();
        if (!PlantStick(id, where, tellPartner: false, network, alongRock: true, outward)) return false;
        TopAnchorSession.RememberCutscene(id, outward, key);
        SendTopAnchor(network, TopAnchorMessage.Cutscene(id, where, outward, key));
        TopAnchorSession.RequestHold();
        TeamAnchorSession.EndPartnership();
        SendTopAnchorLeave(network);
        // The scene may be about to rest. That rest waits so it does not throw this stick away.
        CutsceneStaffSession.ArmStory();
        var why = endsTie ? "the scene would have ended the tie" : "the scene moved her";
        ModLog.Info($"[RopeTeam] Planted a staff for the story scene at ({where.X:F1},{where.Y:F1},{where.Z:F1}): {why}.");
        return true;
    }

    private static void AcceptCutscene(uint id, Vector3 where, Vector3 outward, string key)
    {
        if (string.IsNullOrEmpty(key) || TopAnchorSession.WasCleaned(id) || TopAnchorSession.HasKey(key)) return;
        if (TopAnchorSession.TryGetPosition(id, out _)) return;
        if (!PlantStick(id, where, tellPartner: false, network: null, alongRock: true, outward)) return;
        TopAnchorSession.RememberCutscene(id, outward, key);
        TopAnchorSession.RequestHold();
        TeamAnchorSession.EndPartnership();
        ModLog.Info($"[RopeTeam] Staff for the leader's story scene planted at ({where.X:F1},{where.Y:F1},{where.Z:F1}).");
    }

    internal static void RemoveCutsceneStick(string key)
    {
        if (!TopAnchorSession.TryIdForKey(key, out var id)) return;
        RemoveStick(id, tellPartner: true);
        ModLog.Info("[RopeTeam] The follower started the story scene. The staff came out.");
    }

    private static bool SpawnStaffPiton(uint id, Vector3 lip)
    {
        var lifeline = TryGetLifeline();
        var controller = ResolveLocalClimbController();
        if (lifeline == null || controller == null)
        {
            ModLog.Warning("[RopeTeam] The staff piton was not placed: the lifeline is not ready.");
            return false;
        }

        if (!TryCollectPitonPointers(lifeline, _pitonPointers))
        {
            ModLog.Warning("[RopeTeam] The staff piton was not placed: the placed pitons could not be read.");
            return false;
        }
        try
        {
            lifeline.AddPiton(
                new UnityEngine.Vector3(lip.X, lip.Y, lip.Z),
                Quaternion.identity,
                (PitonExecutionQuality)StaffPitonQuality,
                StaffPitonHp,
                (InventoryItemStringId)(int)InventoryItemStringIdEnum.ITEM_PITON,
                controller);
        }
        catch (Exception exception)
        {
            ModLog.Warning("[RopeTeam] The staff piton could not be placed: " + exception.Message);
            return false;
        }

        var spawned = IntPtr.Zero;
        if (TryCollectPitonPointers(lifeline, _pitonPointersAfterSpawn))
        {
            foreach (var pointer in _pitonPointersAfterSpawn)
            {
                if (_pitonPointers.Contains(pointer)) continue;
                if (spawned == IntPtr.Zero) spawned = pointer;
                else _pitons.Ignore(pointer);
            }
        }

        if (spawned == IntPtr.Zero)
        {
            ModLog.Warning("[RopeTeam] The staff piton was not placed: Cairn added no piton.");
            return false;
        }
        _pitons.Ignore(spawned);
        TopAnchorSession.SetPointer(id, spawned);
        // AddPiton while standing on the ledge does not clip a quickdraw. Without that
        // clip the staff is not a catch once the pitons below it are gone.
        if (!PitonIsOnRope(lifeline, spawned))
            TryAttachTeamPiton(spawned);
        return true;
    }

    private static bool PitonIsOnRope(Il2Cpp.Lifeline lifeline, IntPtr piton)
    {
        if (lifeline == null || piton == IntPtr.Zero) return false;
        try
        {
            return lifeline.AttachedPitonsContains(new Il2Cpp.Piton(piton));
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.piton-on-rope", exception);
            return false;
        }
    }

    private static void DrainStaffPitons()
    {
        TopAnchorSession.DrainDestroy(_staffDestroy);
        foreach (var pointer in _staffDestroy)
        {
            TryDetachPitonViaLifeline(pointer);
            DestroyReleasedPiton(new Piton(pointer));
        }
    }

    /// <summary>
    /// The piton mesh is the fallback picture. It stays hidden only while the stick mesh is still
    /// on the lip. A stick that returned to the hand leaves the piton visible.
    /// </summary>
    private static void PresentStaffPitons()
    {
        TopAnchorSession.CopyIds(_staffIds);
        foreach (var id in _staffIds)
        {
            if (!TopAnchorSession.TryGetPointer(id, out var pointer)) continue;
            if (TopAnchorPlant.StickStayedAtLip(id)) HidePitonRenderers(pointer);
            else ShowPitonRenderers(pointer);
        }
    }

    /// <summary>
    /// The nearest stone around the body, close enough to be the face she is on.
    /// The normal points out of that stone. Her own body is not the rock.
    /// </summary>
    internal static bool TryRockSurface(UnityEngine.Vector3 origin, out UnityEngine.Vector3 point, out UnityEngine.Vector3 outward)
    {
        point = origin;
        outward = UnityEngine.Vector3.up;
        try
        {
            var body = LocalPlayerInterop.TryGetMCGameObject();
            var best = 1.5f;
            var found = false;
            for (var i = 0; i < RockProbes.Length; i++)
            {
                var hits = Physics.RaycastAll(
                    origin, RockProbes[i], best, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                if (hits == null) continue;
                for (var h = 0; h < hits.Length; h++)
                {
                    var hit = hits[h];
                    if (hit.collider == null || hit.distance >= best) continue;
                    var transform = hit.collider.transform;
                    if (body != null && transform != null
                        && (transform == body.transform || transform.IsChildOf(body.transform)))
                        continue;
                    if (hit.normal.sqrMagnitude < 0.01f) continue;
                    best = hit.distance;
                    point = hit.point;
                    outward = hit.normal;
                    found = true;
                }
            }

            return found;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.staff-surface", exception);
            return false;
        }
    }

    private static readonly UnityEngine.Vector3[] RockProbes =
    {
        UnityEngine.Vector3.down,
        UnityEngine.Vector3.up,
        UnityEngine.Vector3.forward,
        UnityEngine.Vector3.back,
        UnityEngine.Vector3.left,
        UnityEngine.Vector3.right,
        new(1f, -1f, 0f),
        new(-1f, -1f, 0f),
        new(0f, -1f, 1f),
        new(0f, -1f, -1f),
    };

    /// <summary>The rock under the standing harness. The climber's own body is not the ledge.</summary>
    private static Vector3 LedgeUnder(Vector3 standing)
    {
        try
        {
            var origin = new UnityEngine.Vector3(standing.X, standing.Y + 0.3f, standing.Z);
            var hits = Physics.RaycastAll(
                origin, UnityEngine.Vector3.down, 3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (hits == null) return standing;
            var body = LocalPlayerInterop.TryGetMCGameObject();
            var best = float.MaxValue;
            var found = false;
            var point = standing;
            for (var i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];
                if (hit.collider == null || hit.distance >= best) continue;
                var transform = hit.collider.transform;
                if (body != null && transform != null
                    && (transform == body.transform || transform.IsChildOf(body.transform)))
                    continue;
                best = hit.distance;
                point = new Vector3(hit.point.x, hit.point.y, hit.point.z);
                found = true;
            }

            return found ? point : standing;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.staff-ledge", exception);
            return standing;
        }
    }

    private static void HidePitonRenderers(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return;
        try
        {
            var piton = new Piton(pointer);
            var renderers = piton.gameObject != null
                ? piton.gameObject.GetComponentsInChildren<Renderer>(true)
                : null;
            if (renderers == null) return;
            for (var i = 0; i < renderers.Length; i++)
                if (renderers[i] != null) renderers[i].enabled = false;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.staff-hide", exception);
        }
    }

    private static void ShowPitonRenderers(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return;
        try
        {
            var piton = new Piton(pointer);
            var renderers = piton.gameObject != null
                ? piton.gameObject.GetComponentsInChildren<Renderer>(true)
                : null;
            if (renderers == null) return;
            for (var i = 0; i < renderers.Length; i++)
                if (renderers[i] != null) renderers[i].enabled = true;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.staff-show", exception);
        }
    }

    private static bool TryChainPointer(uint id, bool own, out IntPtr pointer)
    {
        if (TopAnchorSession.TryGetPointer(id, out pointer)) return true;
        return own ? _pitons.TryGetOwnPointer(id, out pointer) : _pitons.TryGetRemotePointer(id, out pointer);
    }

    private static TopAnchorModule ReadClimbModule()
    {
        var controller = ResolveLocalClimbController();
        if (controller == null) return TopAnchorModule.Other;
        try
        {
            var module = controller.CurrentModule;
            if (module == null) return TopAnchorModule.Other;
            // CurrentModule is boxed as the base wrapper, so GetType() never says Mantling.
            // The native class does, and the module's own ClimbingModule value does too.
            var nativeName = module.GetIl2CppType()?.Name;
            var byName = TopAnchorPolicy.Classify(nativeName);
            if (byName != TopAnchorModule.Other) return byName;
            return TopAnchorPolicy.Classify(module.type.ToString());
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.staff-module", exception);
            return TopAnchorModule.Other;
        }
    }

    private static bool FollowerIsStandingAt(NetworkManager network, Vector3 lip)
    {
        if (network == null || !network.RemotePlayers.TryGetValue(TeamAnchorSession.FollowerId, out var remote)
            || remote == null || !remote.HasPlayerFrame)
            return false;
        if (PawnCaptureInterop.GetPawnStateFromFrame(remote.PlayerFrame) != NetFrame.PawnStateType.Walking)
            return false;
        return TopAnchorPolicy.InVolume(lip, new Vector3(remote.X, remote.Y, remote.Z));
    }

    private static bool PairMember(int playerId)
        => playerId == TeamAnchorSession.LeaderId || playerId == TeamAnchorSession.FollowerId;

    private static void SendTopAnchor(NetworkManager network, byte[] payload)
    {
        network ??= _topAnchorNetwork;
        if (network == null || _topAnchorChannel == 0 || payload == null) return;
        network.SendFeatureStream(_topAnchorChannel, payload, reliable: true);
    }

    private static NetworkManager _topAnchorNetwork;

    private static void DrawHeldLine(NetworkManager network, bool retired)
    {
        if (!TryGetHeldFollowerPosition(network, retired, out var harness))
        {
            HideHeldLine();
            return;
        }

        _heldPoints.Clear();
        _heldPoints.Add(new UnityEngine.Vector3(harness.X, harness.Y, harness.Z));
        if (retired)
        {
            TeamAnchorSession.CopyRetiredPath(_heldChain);
            TeamAnchorSession.CopyRetiredIds(_staffIds);
        }
        else
        {
            TeamAnchorSession.CopyPath(_heldChain);
            TeamAnchorSession.CopyIds(_staffIds);
        }
        for (var i = 0; i < _heldChain.Count; i++)
        {
            var id = i < _staffIds.Count ? _staffIds[i] : 0;
            var point = ChainDrawPoint(id, _heldChain[i]);
            if (float.IsFinite(point.x) && float.IsFinite(point.y) && float.IsFinite(point.z))
                _heldPoints.Add(point);
        }

        if (_heldPoints.Count < 2)
        {
            HideHeldLine();
            return;
        }

        var line = EnsureHeldLine();
        if (line == null) return;
        var span = 0f;
        for (var i = 1; i < _heldPoints.Count; i++)
            span += UnityEngine.Vector3.Distance(_heldPoints[i - 1], _heldPoints[i]);
        TryGetHeldCoil(out var coil);
        DrawRopeSpan(line, _heldPoints, RopeLengthPolicy.IsPaidOut(span, coil));
        if (_heldLineLogged) return;
        _heldLineLogged = true;
        ModLog.Info("[RopeTeam] The follower's rope stays on the chain, points=" + _heldPoints.Count);
    }

    /// <summary>
    /// The harness the held rope starts from. On the follower's own client that is
    /// the local climber. On the leader's client it is the follower's ghost, which
    /// is the only body in view during a practice climb.
    /// </summary>
    private static bool TryGetHeldFollowerPosition(NetworkManager network, bool retired, out Vector3 position)
    {
        position = default;
        var localCatch = retired
            ? TeamAnchorSession.LocalKeepsCatch
            : TeamAnchorSession.IsLocalFollower || TeamAnchorSession.LocalKeepsCatch;
        if (localCatch)
            return TryGetClimberPosition(out position);

        if (!retired && TryGetPracticeFollowerPosition(network, out position))
            return true;

        var followerId = retired ? TeamAnchorSession.LineFollowerId : TeamAnchorSession.FollowerId;
        if (followerId <= 0) return false;
        if (RemotePlayerManager.TryGetGhostHarnessAttachPosition(followerId, out var harness)
            && TryUsePosition(harness, out position))
            return true;
        if (RemotePlayerManager.TryGetGhostTransform(followerId, out var ghost)
            && ghost != null
            && TryUsePosition(ghost.position, out position))
            return true;
        return network != null
               && network.RemotePlayers.TryGetValue(followerId, out var remote)
               && remote != null
               && TryUsePosition(new UnityEngine.Vector3(remote.X, remote.Y, remote.Z), out position);
    }

    private static readonly List<UnityEngine.Vector3> _heldPoints = new();
    private static readonly List<Vector3> _heldChain = new();

    private static LineRenderer EnsureHeldLine()
    {
        if (_heldLine != null)
        {
            if (!_heldLineStyled) _heldLineStyled = StyleHeldLine(_heldLine);
            return _heldLine;
        }

        var shader = Shader.Find("Sprites/Default");
        if (shader == null) return null;
        var root = new GameObject("CairnMP Held Rope");
        UnityEngine.Object.DontDestroyOnLoad(root);
        _heldLine = root.AddComponent<LineRenderer>();
        _heldLine.useWorldSpace = true;
        _heldLine.widthMultiplier = 0.02f;
        _heldLine.material = new Material(shader);
        _heldLine.startColor = _heldLine.endColor = new Color(0.55f, 0.38f, 0.22f, 1f);
        _heldLine.enabled = false;
        _heldLineStyled = StyleHeldLine(_heldLine);
        return _heldLine;
    }

    /// <summary>Use the climber's own rope material so the held line matches the one that just left.</summary>
    private static bool StyleHeldLine(LineRenderer line)
    {
        try
        {
            var source = TeamRopeLine();
            if (source == null || source.sharedMaterial == null) return false;
            line.sharedMaterial = source.sharedMaterial;
            line.widthMultiplier = source.widthMultiplier;
            line.widthCurve = source.widthCurve;
            line.colorGradient = source.colorGradient;
            line.numCapVertices = source.numCapVertices;
            line.numCornerVertices = source.numCornerVertices;
            line.alignment = source.alignment;
            line.textureMode = source.textureMode;
            return true;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.held-line", exception);
            return false;
        }
    }

    private static LineRenderer TeamRopeLine()
    {
        var harness = ResolveLocalHarness();
        var rope = harness != null && harness.PersonalLifeline != null
            ? harness.PersonalLifeline.securingRope
            : null;
        var line = RopeLine(rope);
        if (line != null) return line;
        var climbot = ResolveLocalClimbot();
        return RopeLine(climbot != null ? climbot.GetRope() : null);
    }

    private static LineRenderer RopeLine(LogicalRope rope)
    {
        if (rope == null) return null;
        var line = rope.GetComponent<LineRenderer>();
        if (line != null) return line;
        var prefab = rope.ropePartPrefab;
        return prefab != null ? prefab.GetComponent<LineRenderer>() : null;
    }

    private static void HideHeldLine()
    {
        if (_heldLine != null) _heldLine.enabled = false;
    }

    private static void RememberTopAnchorNetwork(NetworkManager network)
    {
        if (network != null) _topAnchorNetwork = network;
    }
}
