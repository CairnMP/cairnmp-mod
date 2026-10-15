using System;
using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Roping;
using CairnMultiplayerMod.Internal.Networking;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnMultiplayerMod.Internal.Game.Players;

/// <summary>
/// Ghosts spawn early and remain off-screen until posed because Cairn may reject prefab
/// instantiation late in a scene transition.
/// </summary>
internal static class RemotePlayerManager
{
    // The native prefab must stay active: NetplayRemotePlayer.Update applies the
    // current frame to the renderers after SetFrame.
    private const float FirstFrameGraceSeconds = 15f;
    private const float FirstFrameWaitLogIntervalSeconds = 3f;
    private const double PlayerFrameFreshSeconds = 0.5;

    public static bool ShowNames { get; private set; } = true;

    public static bool ToggleNames()
    {
        ShowNames = !ShowNames;
        return ShowNames;
    }

    private class GhostEntry
    {
        public GameObject Root;
        public NetplayRemotePlayer NrpComponent;
        public bool IsRealModel;
        public bool RootPoseFallbackLogged;

        public bool ClimbotLerpApplied;

        public int LastAppliedLampMode;
        public bool HasAppliedLampState;

        public int LastAppliedStickAnchor;
        public bool HasAppliedStickAnchor;

        public int LastAppliedOutfit;
        public bool HasAppliedOutfit;

        public bool LastAppliedGloves;
        public bool HasAppliedCosmeticState;


        public byte[] LastAppliedHandPosePacked;

        public List<LineRenderer> NetRopes;
        public float NextNetRopeScanAt;

        public CapsuleCollider Collider;
        /// <summary>The prefab's own colliders kept for the game's interaction prompts.</summary>
        public List<Collider> InteractionColliders;
        public bool PhysicsActive = true;

        public string LastAppliedNameLabel;
    }

    private class SpawnWaitEntry
    {
        public float FirstSeenAt;
        public float LastLogAt;
        public bool GraceExpiredLogged;
    }

    private static readonly Dictionary<int, GhostEntry> _ghosts = new();
    private static readonly Dictionary<int, SpawnWaitEntry> _spawnWaits = new();
    private static readonly List<int> _playersToRemove = new();
    private static readonly List<int> _placeholders = new();
    private const float PlaceholderUpgradeIntervalSeconds = 5f;
    private static float _nextPlaceholderUpgradeAt;


    private static readonly Vector3 HiddenSpawnPosition = new(0f, -10000f, 0f);

    private static readonly Color[] _palette =
    {
        new(0.20f, 0.80f, 1.00f),
        new(1.00f, 0.80f, 0.20f),
        new(1.00f, 0.30f, 0.70f),
        new(0.40f, 1.00f, 0.40f),
        new(1.00f, 0.40f, 0.30f),
        new(0.80f, 0.60f, 1.00f),
    };

    public static Color ColorForPlayer(int playerId)
    {
        var index = (playerId % _palette.Length + _palette.Length) % _palette.Length;
        return _palette[index];
    }

    public static void Reconcile(NetworkManager net, PlayerState localState)
    {
        if (net == null) return;

        foreach (var kv in net.RemotePlayers)
        {
            var rp = kv.Value;
            if (rp == null || _ghosts.ContainsKey(rp.Id)) continue;
            if (!HasRemotePose(rp))
            {
                TrackSpawnWait(rp, null);
                continue;
            }

            if (localState != PlayerState.InGame)
            {
                TrackSpawnWait(rp, localState);
                continue;
            }

            _spawnWaits.Remove(rp.Id);
            SpawnGhost(rp.Id, rp.Name, rp);
        }

        UpgradePlaceholders(net, localState);

        // Don't remove during Loading/Connecting: Cairn can block a late respawn.
        // We only remove players that have genuinely left.
        _playersToRemove.Clear();
        foreach (var kv in _ghosts)
        {
            if (!net.RemotePlayers.ContainsKey(kv.Key))
                _playersToRemove.Add(kv.Key);
        }
        foreach (var id in _playersToRemove)
        {
            if (_ghosts.TryGetValue(id, out var entry))
            {
                PurgeGhostCosmeticCaches(entry);
                if (entry.Root != null) Object.Destroy(entry.Root);
                _ghosts.Remove(id);
                _spawnWaits.Remove(id);
                ModLog.Debug($"[Ghost] Despawned player {id} (left session)");
            }
        }
    }

    /// <summary>
    /// A ghost built while Cairn's climber prefab was out of reach is only a stand-in — no
    /// harness, so no rope, no name and no revive prompt — and nothing used to replace it.
    /// Once the prefab can be found again, the stand-in is rebuilt as the real climber.
    /// </summary>
    private static void UpgradePlaceholders(NetworkManager net, PlayerState localState)
    {
        if (localState != PlayerState.InGame) return;
        _placeholders.Clear();
        foreach (var kv in _ghosts)
            if (kv.Value != null && !kv.Value.IsRealModel) _placeholders.Add(kv.Key);
        if (_placeholders.Count == 0 || Time.unscaledTime < _nextPlaceholderUpgradeAt) return;
        // A prefab that is found but still cannot be instantiated must not have stand-ins
        // destroyed and rebuilt every frame.
        _nextPlaceholderUpgradeAt = Time.unscaledTime + PlaceholderUpgradeIntervalSeconds;
        if (NetplayAnimationInterop.TryGetNetplayClimberPrefab() == null) return;

        foreach (var id in _placeholders)
        {
            if (!net.RemotePlayers.TryGetValue(id, out var rp) || !HasRemotePose(rp)) continue;
            if (_ghosts.TryGetValue(id, out var standIn))
            {
                SetGhostPhysicsActive(standIn, false);
                if (standIn.Root != null) Object.Destroy(standIn.Root);
                _ghosts.Remove(id);
            }
            SpawnGhost(id, rp.Name, rp);
            ModLog.Debug($"[Ghost] Rebuilt {id}'s stand-in as the native climber");
        }
    }

    private static void TrackSpawnWait(RemotePlayer rp, PlayerState? localState)
    {
        var now = Time.unscaledTime;
        if (!_spawnWaits.TryGetValue(rp.Id, out var wait))
        {
            wait = new SpawnWaitEntry
            {
                FirstSeenAt = now,
                LastLogAt = now,
            };
            _spawnWaits[rp.Id] = wait;
            return;
        }

        var age = now - wait.FirstSeenAt;
        if (age < FirstFrameWaitLogIntervalSeconds) return;

        var reason = localState.HasValue
            ? $"localState={localState.Value}"
            : $"state={rp.State} lastState={FormatLastUpdateAge(rp)}";

        if (age >= FirstFrameGraceSeconds && !wait.GraceExpiredLogged)
        {
            wait.GraceExpiredLogged = true;
            wait.LastLogAt = now;
            ModLog.Warning($"[Ghost] Still waiting before spawning {rp.Id} ({rp.Name}) after {age:0.0}s {reason}");
            return;
        }

        if (now - wait.LastLogAt < FirstFrameWaitLogIntervalSeconds) return;

        wait.LastLogAt = now;
        ModLog.Debug($"[Ghost] Waiting before spawning {rp.Id} ({rp.Name}) age={age:0.0}s {reason}");
    }

    private static string FormatLastUpdateAge(RemotePlayer rp)
    {
        if (rp == null || rp.LastUpdateTime <= 0) return "never";

        var now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        return $"{Math.Max(0, now - rp.LastUpdateTime):0.0}s ago";
    }

    public static bool TryGetGhostHarnessAttachPosition(int playerId, out Vector3 pos)
    {
        pos = default;
        return TryGetGhostHarness(playerId, out var harness) && RopeInterop.TryGetHarnessAttachPosition(harness, out pos);
    }

    /// <summary>
    /// The ghost's root transform, for anything that needs to point at a remote climber --
    /// the spectator camera today. Only a fully built ghost qualifies: the placeholder has no
    /// body worth watching.
    /// </summary>
    internal static bool TryGetGhostTransform(int playerId, out UnityEngine.Transform ghost)
    {
        ghost = null;
        if (!_ghosts.TryGetValue(playerId, out var entry)) return false;
        if (!entry.IsRealModel || entry.Root == null) return false;
        try
        {
            ghost = entry.Root.transform;
            return ghost != null;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("remote-player.resolve-ghost-transform", exception);
            return false;
        }
    }

    internal static bool TryGetGhostHarness(int playerId, out Il2Cpp.Harness harness)
    {
        harness = null;
        if (!_ghosts.TryGetValue(playerId, out var entry)) return false;
        if (!entry.IsRealModel || entry.NrpComponent == null) return false;
        try
        {
            harness = entry.NrpComponent.Harness;
            return harness != null;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("remote-player.resolve-harness-position", exception);
            return false;
        }
    }

    private static bool ShouldApplyPose(RemotePlayer rp, PlayerState localState)
    {
        if (localState != PlayerState.InGame) return false;
        return HasRemotePose(rp);
    }

    private static bool HasRemotePose(RemotePlayer rp)
    {
        if (rp == null) return false;
        if (rp.State == PlayerState.Unknown || rp.State == PlayerState.InMenu || rp.State == PlayerState.Loading) return false;
        // A camper reports no pose at all (their presence packets carry 0,0,0), while their
        // last frame still looks renderable — without this the ghost and its collider were
        // parked at the world origin for the whole camp.
        if (rp.State == PlayerState.Bivouac) return false;
        if (HasRenderableFrame(rp)) return true;
        if (rp.LastUpdateTime <= 0) return false;
        return true;
    }

    private static bool HasRenderableFrame(RemotePlayer rp)
    {
        return rp != null
            && rp.HasPlayerFrame
            && rp.PlayerFrame.IsValid
            && rp.PlayerFrame.Positions != null
            && rp.PlayerFrame.Positions.Length >= 3;
    }

    private static void ApplyInitialTransform(GameObject go, RemotePlayer rp)
    {
        if (go == null) return;
        if (!HasRemotePose(rp))
        {
            go.transform.position = HiddenSpawnPosition;
            return;
        }

        go.transform.position = new Vector3(rp.X, rp.Y, rp.Z);
        go.transform.rotation = Quaternion.Euler(0f, rp.YawDeg, 0f);
    }

    public static void UpdateAll(NetworkManager net, PlayerState localState, Func<int, bool> isSpeaking = null)
    {
        foreach (var kv in net.RemotePlayers)
        {
            if (!_ghosts.TryGetValue(kv.Key, out var entry)) continue;
            if (entry.Root == null) continue;

            var rp = kv.Value;
            if (!ShouldApplyPose(rp, localState))
            {
                // Loading / menu / stale peer: park the ghost far away AND take its capsule
                // out of the physics scene, so the teleport never touches PhysX while Cairn
                // is swapping scenes.
                SetGhostPhysicsActive(entry, false);
                entry.Root.transform.position = HiddenSpawnPosition;
                continue;
            }

            SetGhostPhysicsActive(entry, true);

            if (entry.IsRealModel)
                RecolorGhostNetRopes(entry);

            var frameRefused = false;
            if (entry.IsRealModel && entry.NrpComponent != null && HasFreshPlayerFrame(rp))
            {
                var poseApplied = NetplayAnimationInterop.CallNetplaySetFrame(
                    entry.NrpComponent, rp.Id, rp.Name, rp.PlayerFrame);

                if (rp.HasClimbotFrame)
                {
                    NetplayAnimationInterop.CallNetplayClimbotSetFrame(entry.NrpComponent, rp.Id, rp.ClimbotFrame);

                    // Enable the climbot's native interpolation once it exists (created on
                    // the first frame) — otherwise it snaps while the player is smoothed.
                    if (!entry.ClimbotLerpApplied)
                    {
                        try
                        {
                            var climbot = entry.NrpComponent.Climbot;
                            if (climbot != null)
                            {
                                climbot.lerp = true;
                                if (climbot.lerpSpeed <= 0f) climbot.lerpSpeed = 12f;
                                entry.ClimbotLerpApplied = true;
                            }
                        }
                        catch (Exception exception) { ModLog.SuppressedException("remote-player.apply-runtime-lerp", exception); }
                    }
                }

                if (poseApplied) continue;
                frameRefused = true;

                // Neither the native SetFrame nor the direct bone write took the frame
                // (native assertion, bone-count mismatch...). Falling through keeps the
                // ghost where its owner actually is instead of leaving it frozen wherever
                // the last accepted frame put it.
            }

            ApplyRootPoseFallback(entry, rp, frameRefused
                ? "the frame was refused by every apply path"
                : "the player NetFrame is stale");
        }

        ApplyPendingLampStates(net);
        ApplyPendingCosmetics(net);
        ApplyPendingFingerPoses(net);

        CosmeticInterop.TickGhostGloveRigs();

        // Both the visibility and the label are asserted after the frames are applied. The
        // label is written only here — the SetFrame patch no longer touches it — so the
        // last-applied cache stays true to what the mesh shows.
        foreach (var kv in net.RemotePlayers)
        {
            if (!_ghosts.TryGetValue(kv.Key, out var entry)) continue;
            if (!entry.IsRealModel || entry.NrpComponent == null) continue;

            NetplayAnimationInterop.SetGhostNameVisible(entry.NrpComponent, ShowNames);

            var speaking = isSpeaking != null && isSpeaking(kv.Key);
            entry.LastAppliedNameLabel = NetplayAnimationInterop.SetGhostNameLabel(
                entry.NrpComponent, kv.Value?.Name, speaking, entry.LastAppliedNameLabel);
        }
    }

    /// <summary>The native update restores debug green, so white must be reasserted after it.</summary>
    private static void RecolorGhostNetRopes(GhostEntry entry)
    {
        if (entry.Root == null) return;

        if (entry.NetRopes != null && (entry.NetRopes.Count == 0 || entry.NetRopes[0] == null))
            entry.NetRopes = null;

        try
        {
            if (entry.NetRopes == null)
            {
                var now = Time.unscaledTime;
                if (now < entry.NextNetRopeScanAt) return;
                entry.NextNetRopeScanAt = now + 0.5f;

                var lines = entry.Root.GetComponentsInChildren<LineRenderer>(true);
                if (lines == null) return;

                List<LineRenderer> found = null;
                for (int i = 0; i < lines.Length; i++)
                {
                    var lr = lines[i];
                    if (lr == null) continue;
                    var name = lr.gameObject.name;
                    if (name != null && name.StartsWith("NetLogicalRope", StringComparison.Ordinal))
                        (found ??= new List<LineRenderer>()).Add(lr);
                }
                if (found == null) return;
                entry.NetRopes = found;
            }

            for (int i = 0; i < entry.NetRopes.Count; i++)
            {
                var lr = entry.NetRopes[i];
                if (lr == null) continue;
                lr.startColor = Color.white;
                lr.endColor = Color.white;
            }
        }
        catch (Exception exception) { ModLog.SuppressedException("remote-player.recolor-ropes", exception); }
    }

    private static void ApplyPendingFingerPoses(NetworkManager net)
    {
        foreach (var kv in net.RemotePlayers)
        {
            var rp = kv.Value;
            if (rp == null || !rp.HasHandPose || rp.HandPosePacked == null) continue;
            if (!_ghosts.TryGetValue(kv.Key, out var entry)) continue;
            if (!entry.IsRealModel || entry.NrpComponent == null) continue;
            if (ReferenceEquals(entry.LastAppliedHandPosePacked, rp.HandPosePacked)) continue;

            if (FingerInterop.TryApplyRemotePose(entry.NrpComponent, rp.HandPosePacked))
                entry.LastAppliedHandPosePacked = rp.HandPosePacked;
        }
    }

    private static void ApplyPendingLampStates(NetworkManager net)
    {
        foreach (var kv in net.RemotePlayers)
        {
            var rp = kv.Value;
            if (rp == null || !rp.HasLampState) continue;
            if (!_ghosts.TryGetValue(kv.Key, out var entry)) continue;
            if (!entry.IsRealModel || entry.NrpComponent == null) continue;

            // The lamp int carries the light mode (bits 0-7) + the stick anchor mode (bits
            // 8-15) + the outfit bitfield (bits 16-24, cf. PlayerStateBroadcaster). We decode
            // and apply each separately.
            int lightMode = rp.LampMode & 0xFF;
            int anchorMode = (rp.LampMode >> 8) & 0xFF;
            int outfitBits = (rp.LampMode >> 16) & CosmeticInterop.OutfitBitsMask;

            ApplyLampMode(entry, lightMode);
            ApplyStickAnchor(entry, anchorMode);
            ApplyOutfit(entry, outfitBits);
        }
    }

    private static void ApplyLampMode(GhostEntry entry, int lightMode)
    {
        if (entry.HasAppliedLampState && entry.LastAppliedLampMode == lightMode) return;
        if (!LampInterop.TryApplyRemoteState(entry.NrpComponent, lightMode)) return;
        entry.LastAppliedLampMode = lightMode;
        entry.HasAppliedLampState = true;
    }

    private static void ApplyStickAnchor(GhostEntry entry, int anchorMode)
    {
        if (entry.HasAppliedStickAnchor && entry.LastAppliedStickAnchor == anchorMode) return;
        if (!CosmeticInterop.ApplyGhostStickByAnchorMode(entry.NrpComponent, anchorMode)) return;
        entry.LastAppliedStickAnchor = anchorMode;
        entry.HasAppliedStickAnchor = true;
    }

    private static void ApplyOutfit(GhostEntry entry, int outfitBits)
    {
        if (entry.HasAppliedOutfit && entry.LastAppliedOutfit == outfitBits) return;
        if (!CosmeticInterop.ApplyGhostOutfitBits(entry.NrpComponent, outfitBits)) return;
        entry.LastAppliedOutfit = outfitBits;
        entry.HasAppliedOutfit = true;
    }

    private static void ApplyPendingCosmetics(NetworkManager net)
    {
        foreach (var kv in net.RemotePlayers)
        {
            var rp = kv.Value;
            if (rp == null || !rp.HasCosmeticState) continue;
            if (!_ghosts.TryGetValue(kv.Key, out var entry)) continue;
            if (!entry.IsRealModel || entry.NrpComponent == null) continue;

            bool glovesOn = (rp.CosmeticFlags & Protocol.CosmeticFlagGlowingGloves) != 0;
            if (!entry.HasAppliedCosmeticState || entry.LastAppliedGloves != glovesOn)
            {
                // Glove mesh (cloned rig) + glow (synthetic point-lights at the hands, reliable —
                // cloning the real lights drifted into nothing because of their follow script).
                bool ok = CosmeticInterop.SetGhostGloveMesh(entry.NrpComponent, glovesOn);
                CosmeticInterop.SetGhostGlowingGloves(entry.NrpComponent, glovesOn);
                if (ok)
                {
                    entry.LastAppliedGloves = glovesOn;
                    entry.HasAppliedCosmeticState = true;
                }
            }
        }
    }

    private static bool HasFreshPlayerFrame(RemotePlayer rp)
    {
        if (!HasRenderableFrame(rp)) return false;
        if (rp.LastPlayerFrameTime <= 0) return true;

        var now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        return now - rp.LastPlayerFrameTime <= PlayerFrameFreshSeconds;
    }

    /// <summary>Position fallback deliberately leaves the rig in bind pose instead of freezing it elsewhere.</summary>
    private static void ApplyRootPoseFallback(GhostEntry entry, RemotePlayer rp, string reason)
    {
        entry.Root.transform.position = new Vector3(rp.X, rp.Y, rp.Z);
        entry.Root.transform.rotation = Quaternion.Euler(0f, rp.YawDeg, 0f);

        if (entry.IsRealModel && !entry.RootPoseFallbackLogged)
        {
            entry.RootPoseFallbackLogged = true;
            ModLog.Warning($"[Ghost] Using root pose fallback for {rp.Id} ({rp.Name}): {reason}");
        }
    }

    /// <summary>
    /// Detaches every ghost from the physics scene. Called as soon as Cairn leaves gameplay,
    /// because UpdateAll stops running before the scene is actually torn down.
    /// </summary>
    public static void SuspendPhysics()
    {
        foreach (var entry in _ghosts.Values)
            SetGhostPhysicsActive(entry, false);
    }

    public static void ClearAll()
    {
        SuspendPhysics();
        foreach (var kv in _ghosts)
            if (kv.Value?.Root != null) Object.Destroy(kv.Value.Root);
        _ghosts.Clear();
        _spawnWaits.Clear();
        _playersToRemove.Clear();
        CosmeticInterop.ResetGhostCosmeticCaches();
    }

    public static string DebugSummary()
    {
        return $"ghosts={_ghosts.Count} waits={_spawnWaits.Count}";
    }

    public static bool IsManagedNetplayPlayer(NetplayRemotePlayer player)
    {
        if (player == null || player.Pointer == IntPtr.Zero)
            return false;

        foreach (var entry in _ghosts.Values)
        {
            if (entry?.NrpComponent == null)
                continue;
            if (entry.NrpComponent.Pointer == player.Pointer)
                return true;
        }

        return false;
    }

    public static bool IsManagedNetplayClimbot(NetplayRemoteClimbot climbot)
    {
        if (climbot == null || climbot.Pointer == IntPtr.Zero)
            return false;

        foreach (var entry in _ghosts.Values)
        {
            if (entry?.NrpComponent == null)
                continue;

            try
            {
                var managedClimbot = entry.NrpComponent.Climbot;
                if (managedClimbot != null && managedClimbot.Pointer == climbot.Pointer)
                    return true;
            }
            catch (Exception exception)
            {
                ModLog.SuppressedException("remote-player.match-destroying-climbot", exception);
            }
        }

        return false;
    }


    public static void OnPlayerJoined(int id, string name, RemotePlayer rp = null)
    {
        ModLog.Debug($"[Ghost] Player {id} ({name}) joined -- waiting for first NetFrame");
    }

    public static void OnPlayerLeft(int id)
    {
        if (_ghosts.TryGetValue(id, out var entry))
        {
            PurgeGhostCosmeticCaches(entry);
            if (entry.Root != null) Object.Destroy(entry.Root);
            _ghosts.Remove(id);
            ModLog.Debug($"[Ghost] Despawned player {id}");
        }
        _spawnWaits.Remove(id);
    }


    private static void PurgeGhostCosmeticCaches(GhostEntry entry)
    {
        if (entry?.NrpComponent == null) return;
        try { CosmeticInterop.ResetGhostCosmeticCaches(entry.NrpComponent); }
        catch (Exception exception) { ModLog.SuppressedException("remote-player.purge-cosmetic-cache", exception); }
    }

    private static void SpawnGhost(int id, string name, RemotePlayer rp)
    {
        if (_ghosts.ContainsKey(id)) return;
        GameObject go = null;
        try
        {
            var prefab = NetplayAnimationInterop.TryGetNetplayClimberPrefab();

            bool isRealModel = false;

            if (prefab != null)
            {
                go = Object.Instantiate(prefab);
                go.name = $"MP_Ghost_{id}_{name}";

                // Keep the native behaviours active: their Update applies the
                // current frame to the mesh after our SetFrame calls.
                var nrpComp = go.GetComponent<NetplayRemotePlayer>()
                    ?? go.GetComponentInChildren<NetplayRemotePlayer>(true);

                if (nrpComp == null)
                {
                    ModLog.Warning("[Ghost] NetplayRemotePlayer component missing from native prefab");
                    Object.Destroy(go);
                    go = null;
                }

                if (go != null)
                {
                    go.SetActive(true);
                    ActivateNativeRemoteVisuals(go);

                    // Network-rate frame writes need native interpolation to avoid visible snapping.
                    if (nrpComp != null)
                    {
                        try
                        {
                            ModLog.Debug($"[Ghost] native lerp defaults: lerp={nrpComp.lerp} lerpSpeed={nrpComp.lerpSpeed}");
                            nrpComp.lerp = true;
                            if (nrpComp.lerpSpeed <= 0f) nrpComp.lerpSpeed = 12f;
                        }
                        catch (Exception exception) { ModLog.SuppressedException("remote-player.apply-spawn-lerp", exception); }

                        // Use Cairn's palette so custom shaders receive the expected material setup.
                        try
                        {
                            var skin = nrpComp.skin != null
                                ? nrpComp.skin
                                : go.GetComponentInChildren<NetplayPlayerSkin>(true);
                            if (skin != null)
                            {
                                byte colorIndex = (byte)(((id % 6) + 6) % 6);
                                skin.SetColorIndex(colorIndex);
                                ModLog.Debug($"[Ghost] native skin color index {colorIndex} applied to {id}");
                            }
                        }
                        catch (Exception exception) { ModLog.SuppressedException("remote-player.apply-native-skin", exception); }
                    }

                    var skinned = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                    for (int i = 0; i < skinned.Count; i++)
                    {
                        var r = skinned[i];
                        if (r == null) continue;
                        try { r.enabled = true; r.updateWhenOffscreen = true; }
                        catch (Exception exception) { ModLog.SuppressedException("remote-player.enable-renderer", exception); }
                    }

                    var cameras = go.GetComponentsInChildren<Camera>(true);
                    for (int i = 0; i < cameras.Count; i++) Object.Destroy(cameras[i]);

                    var colliders = go.GetComponentsInChildren<Collider>(true);
                    for (int i = 0; i < colliders.Count; i++)
                    {
                        try { colliders[i].enabled = false; }
                        catch (Exception exception) { ModLog.SuppressedException("remote-player.disable-prefab-collider", exception); }
                    }
                    var interactionColliders = RestoreInteractionColliders(go, id);
                    var capsule = AddPlayerCollider(go);

                    isRealModel = true;
                    ModLog.Debug($"[Ghost] Spawned native Cairn player for {id} ({name})");

                    ApplyInitialTransform(go, rp);

                    Object.DontDestroyOnLoad(go);
                    _ghosts[id] = new GhostEntry
                    {
                        Root = go,
                        NrpComponent = nrpComp,
                        IsRealModel = true,
                        Collider = capsule,
                        InteractionColliders = interactionColliders,
                    };
                }
            }

            if (!isRealModel || go == null)
            {
                if (go != null) Object.Destroy(go);
                go = PrimitiveHumanoidFactory.Build(id, name, _palette[id % _palette.Length]);

                ApplyInitialTransform(go, rp);

                Object.DontDestroyOnLoad(go);
                _ghosts[id] = new GhostEntry
                {
                    Root = go,
                    NrpComponent = null,
                    IsRealModel = false,
                };
                ModLog.Debug($"[Ghost] Fallback primitive humanoid for player {id} ({name})");
            }
        }
        catch (Exception ex)
        {
            ModLog.Error($"[Ghost] Spawn failed for {id}: {ex.Message}");
            if (go != null) Object.Destroy(go);
        }
    }

    private static void ActivateNativeRemoteVisuals(GameObject root)
    {
        if (root == null) return;
        SetChildActive(root, "Visual");
        SetChildActive(root, "NetplayClimbot");
    }

    private static void SetChildActive(GameObject root, string childName)
    {
        try
        {
            var child = root.transform.Find(childName);
            if (child != null)
                child.gameObject.SetActive(true);
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[Ghost] Could not activate child '{childName}': {ex.Message}");
        }
    }

    /// <summary>
    /// The game finds what a climber can interact with — the revive prompt on a body among
    /// them — through the provider's own colliders, which the blanket shutdown above also
    /// switched off. They come back as triggers: reachable, but never something to bump
    /// into. Called before the ghost's kinematic body is added, they ride on it like the
    /// capsule does instead of standing in the static physics tree.
    /// </summary>
    private static List<Collider> RestoreInteractionColliders(GameObject ghost, int id)
    {
        var restored = new List<Collider>();
        try
        {
            var providers = ghost.GetComponentsInChildren<NetplayRemotePlayerInteractionProvider>(true);
            for (int p = 0; p < providers.Count; p++)
            {
                var provider = providers[p];
                if (provider == null) continue;
                var before = restored.Count;
                var own = provider.GetComponents<Collider>();
                for (int i = 0; i < own.Count; i++) RestoreAsTrigger(own[i], restored);
                if (restored.Count > before) continue;

                var nested = provider.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < nested.Count; i++) RestoreAsTrigger(nested[i], restored);
            }

            ModLog.Debug(restored.Count == 0
                ? $"[Ghost] {id}'s interaction provider carries no collider to restore"
                : $"[Ghost] Restored {restored.Count} interaction collider(s) on {id}'s ghost");
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("remote-player.restore-interaction-colliders", exception);
        }
        return restored;
    }

    private static void RestoreAsTrigger(Collider collider, List<Collider> restored)
    {
        if (collider == null) return;
        collider.isTrigger = true;
        collider.enabled = true;
        restored.Add(collider);
    }

    private static CapsuleCollider AddPlayerCollider(GameObject ghost)
    {
        try
        {
            // A collider without a Rigidbody is a STATIC actor for PhysX, and the ghost is
            // teleported every frame. Moving a static actor forces PhysX to rebuild its
            // static AABB tree on a worker thread; doing that while the gameplay scene is
            // being unloaded (or while a ghost spawns mid-transition) crashes the engine
            // inside the pruner. A kinematic Rigidbody makes the capsule a moving actor,
            // which is the supported way to carry a collider that changes position.
            // A prefab that already brings a body gets ours folded into it: a second
            // AddComponent<Rigidbody> returns nothing and would leave the capsule out.
            var body = ghost.GetComponent<Rigidbody>() ?? ghost.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;
            body.collisionDetectionMode = CollisionDetectionMode.Discrete;

            var col = ghost.AddComponent<CapsuleCollider>();
            col.center = new Vector3(0f, 0.9f, 0f);
            col.radius = 0.3f;
            col.height = 1.8f;
            col.direction = 1;
            return col;
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[Ghost] AddPlayerCollider failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Keeps the ghost's capsule out of the physics scene whenever the ghost is not being
    /// posed. Ghosts are DontDestroyOnLoad, so without this their actors stay registered
    /// while Cairn tears the gameplay scene (and its physics scene) down.
    /// </summary>
    private static void SetGhostPhysicsActive(GhostEntry entry, bool active)
    {
        if (entry == null || entry.PhysicsActive == active) return;

        try
        {
            if (entry.InteractionColliders != null)
                foreach (var collider in entry.InteractionColliders)
                    if (collider != null) collider.enabled = active;

            if (entry.Collider == null) return;

            entry.Collider.enabled = active;
            entry.PhysicsActive = active;
        }
        catch (Exception exception) { ModLog.SuppressedException("remote-player.toggle-ghost-physics", exception); }
    }
}
