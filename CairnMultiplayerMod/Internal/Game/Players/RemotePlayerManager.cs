using CairnMultiplayerMod.Internal.Diagnostics;
using System;
using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Game.Roping;
using CairnMultiplayerMod.Internal.Networking;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnMultiplayerMod.Internal.Game.Players;

/// <summary>
/// Manager for remote ghosts. The objects are created as soon as the remote player
/// is known, then kept alive across loads and animations.
///
/// Cairn can block the spawn if the remote player is instantiated too late after
/// scene transitions. So we favor an early spawn, with off-screen placement until
/// a usable pose has been received.
///
/// Tries the real Cairn model first (MC_Netplay_Player.prefab), then falls back to
/// a primitive humanoid if the prefab isn't available.
/// </summary>
internal static class RemotePlayerManager
{
    // The native prefab must stay active: NetplayRemotePlayer.Update applies the
    // current frame to the renderers after SetFrame.
    private const float FirstFrameGraceSeconds = 15f;
    private const float FirstFrameWaitLogIntervalSeconds = 3f;
    private const double PlayerFrameFreshSeconds = 0.5;

    /// <summary>
    /// Whether to show name plates above remote ghosts. Toggled with the N key
    /// (useful in photo mode for clean shots). Applied every frame in UpdateAll to
    /// each ghost's native nameMesh.
    /// </summary>
    public static bool ShowNames { get; private set; } = true;

    /// <summary>Toggles the display of remote player names. Returns the new state.</summary>
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

        // Native interpolation applied once on the remote climbot (created lazily on
        // the first climbot frame received) — same smoothing as the player.
        public bool ClimbotLerpApplied;

        // Last mode applied to the AavaLightStick component — used to deduplicate
        // SetMode calls on the ghost side.
        public int LastAppliedLampMode;
        public bool HasAppliedLampState;

        // Last stick anchor mode applied (Locator/Default) — dedup, cf. ApplyPendingLampStates.
        public int LastAppliedStickAnchor;
        public bool HasAppliedStickAnchor;

        // Last outfit bitfield applied (active meshes) — dedup, cf. ApplyPendingLampStates.
        public int LastAppliedOutfit;
        public bool HasAppliedOutfit;

        // Last glowing-gloves state applied to the ghost — dedup of the calls.
        public bool LastAppliedGloves;
        public bool HasAppliedCosmeticState;


        // Last finger pose applied (reference of the received array) — used to
        // reapply only on change (fingers persist between frames).
        public byte[] LastAppliedHandPosePacked;

        // The ghost's netplay ropes (NetLogicalRope*, green by default) to recolor
        // white. Cached once found; scan throttled while null.
        public List<LineRenderer> NetRopes;
        public float NextNetRopeScanAt;
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


    private static readonly Vector3 HiddenSpawnPosition = new(0f, -10000f, 0f);

    private static readonly Color[] _palette =
    {
        new(0.20f, 0.80f, 1.00f), // cyan blue
        new(1.00f, 0.80f, 0.20f), // amber yellow
        new(1.00f, 0.30f, 0.70f), // pink
        new(0.40f, 1.00f, 0.40f), // light green
        new(1.00f, 0.40f, 0.30f), // coral
        new(0.80f, 0.60f, 1.00f), // purple
    };

    /// <summary>
    /// Stable color associated with a player id (same palette as the ghosts).
    /// Reused by ping markers to identify the author by color.
    /// </summary>
    public static Color ColorForPlayer(int playerId)
    {
        var index = (playerId % _palette.Length + _palette.Length) % _palette.Length;
        return _palette[index];
    }

    /// <summary>
    /// Called every frame while connected. Spawns ghosts as soon as the network
    /// knows a remote player and keeps them alive across transitions. Ghosts only
    /// despawn if the player leaves.
    /// </summary>
    public static void Reconcile(NetworkManager net, PlayerState localState)
    {
        if (net == null) return;

        // Create the ghosts as soon as the network knows a remote player.
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

    /// <summary>
    /// World position of the rope's attach point on a ghost's harness
    /// (NetplayRemoteHarness.GetAttachPosition()). Used to rope the inter-player
    /// rope to the real harness, like the game does. False if the ghost / its
    /// harness isn't available.
    /// </summary>
    public static bool TryGetGhostHarnessAttachPosition(int playerId, out Vector3 pos)
    {
        pos = default;
        if (!_ghosts.TryGetValue(playerId, out var entry)) return false;
        if (!entry.IsRealModel || entry.NrpComponent == null) return false;
        try
        {
            var harness = entry.NrpComponent.Harness;
            return harness != null && RopeInterop.TryGetHarnessAttachPosition(harness, out pos);
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

    /// <summary>
    /// Updates each ghost's transform from the latest network data.
    /// Called every frame after Reconcile.
    /// </summary>
    public static void UpdateAll(NetworkManager net, PlayerState localState)
    {
        foreach (var kv in net.RemotePlayers)
        {
            if (!_ghosts.TryGetValue(kv.Key, out var entry)) continue;
            if (entry.Root == null) continue;

            var rp = kv.Value;
            if (!ShouldApplyPose(rp, localState))
            {
                entry.Root.transform.position = HiddenSpawnPosition;
                continue;
            }

            // Recolor the ghost's green netplay rope to white.
            if (entry.IsRealModel)
                RecolorGhostNetRopes(entry);

            // Drive the animation via the game's native SetFrame pipeline.
            if (entry.IsRealModel && entry.NrpComponent != null && HasFreshPlayerFrame(rp))
            {
                NetplayAnimationInterop.CallNetplaySetFrame(entry.NrpComponent, rp.Id, rp.Name, rp.PlayerFrame);

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

                continue;
            }

            ApplyRootPoseFallback(entry, rp);
        }

        ApplyPendingLampStates(net);
        ApplyPendingCosmetics(net);
        ApplyPendingFingerPoses(net);

        // Re-seat the ghosts' glove rigs onto their body bones (after this frame's SetFrames).
        CosmeticInterop.TickGhostGloveRigs();

        // Name-plate state: applied AFTER all this frame's SetFrames (the native code
        // can re-enable the nameMesh), to always have the last word and avoid flickering.
        // Only affects real-model ghosts (native nameMesh field).
        foreach (var kv in net.RemotePlayers)
        {
            if (!_ghosts.TryGetValue(kv.Key, out var entry)) continue;
            if (entry.IsRealModel && entry.NrpComponent != null)
                NetplayAnimationInterop.SetGhostNameVisible(entry.NrpComponent, ShowNames);
        }
    }

    /// <summary>
    /// Recolors the ghost's netplay rope (LineRenderers named NetLogicalRope*, debug
    /// green RGBA(0,1,0.016) by default) to white, to match the local rope
    /// (LogicalRope). The GetComponentsInChildren scan is throttled to 2 Hz while
    /// nothing is found (the rope can be created late); once found, we reassert white
    /// every frame in case the game greens the rope back (negligible cost).
    /// </summary>
    private static void RecolorGhostNetRopes(GhostEntry entry)
    {
        if (entry.Root == null) return;

        // Invalid cache (rope destroyed/recreated) -> restart a scan.
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

    /// <summary>
    /// Applies the received finger poses to the ghosts, after the native pipeline
    /// (SetFrame) that poses the body. Only reapplies on change: the fingers'
    /// localRotation persist between frames (they aren't touched by the body's bone set).
    /// </summary>
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

    /// <summary>
    /// Applies the received cosmetic state (glowing gloves for now) to the ghosts.
    /// Only reapplies on change, like the lamp.
    /// </summary>
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

    private static void ApplyRootPoseFallback(GhostEntry entry, RemotePlayer rp)
    {
        entry.Root.transform.position = new Vector3(rp.X, rp.Y, rp.Z);
        entry.Root.transform.rotation = Quaternion.Euler(0f, rp.YawDeg, 0f);

        if (entry.IsRealModel && !entry.RootPoseFallbackLogged)
        {
            entry.RootPoseFallbackLogged = true;
            ModLog.Warning($"[Ghost] Using root pose fallback for {rp.Id} ({rp.Name}) while player NetFrame is stale");
        }
    }

    /// <summary>Destroys all ghosts — called on disconnect.</summary>
    public static void ClearAll()
    {
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

    // -- Tracking callbacks (no visual work) ---------------------------

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

    // -- Internal ---------------------------------------------------------

    /// <summary>Drops the cosmetic module's caches for this ghost before its GameObject dies.</summary>
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
            // Try the real Cairn prefab first. On failure or if it's invisible,
            // fall back to the primitive humanoid.
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

                // Enable the visuals expected by the native prefab.
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

                    // Native interpolation: the prefab smooths the movement toward currentFrame in
                    // its Update(). We enable it explicitly (the mod writes currentFrame inline at
                    // network rate; without lerp the render snaps on every packet). We preserve an
                    // already-configured lerpSpeed, otherwise a reasonable fallback. Log the defaults for tuning.
                    if (nrpComp != null)
                    {
                        try
                        {
                            ModLog.Debug($"[Ghost] native lerp defaults: lerp={nrpComp.lerp} lerpSpeed={nrpComp.lerpSpeed}");
                            nrpComp.lerp = true;
                            if (nrpComp.lerpSpeed <= 0f) nrpComp.lerpSpeed = 12f;
                        }
                        catch (Exception exception) { ModLog.SuppressedException("remote-player.apply-spawn-lerp", exception); }

                        // Native skin: tint each remote player with the game's official palette
                        // (NetplayPlayerSkin.SetColorIndex) instead of the mod's manual palette.
                        // Stable per-player index (conservative modulo to stay in range).
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

                    // Force updateWhenOffscreen on the SkinnedMeshes + enable the renderers.
                    var skinned = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                    for (int i = 0; i < skinned.Count; i++)
                    {
                        var r = skinned[i];
                        if (r == null) continue;
                        try { r.enabled = true; r.updateWhenOffscreen = true; }
                        catch (Exception exception) { ModLog.SuppressedException("remote-player.enable-renderer", exception); }
                    }

                    // Remove the cameras.
                    var cameras = go.GetComponentsInChildren<Camera>(true);
                    for (int i = 0; i < cameras.Count; i++) Object.Destroy(cameras[i]);

                    // Disable the prefab's colliders, add our own capsule collider.
                    var colliders = go.GetComponentsInChildren<Collider>(true);
                    for (int i = 0; i < colliders.Count; i++)
                    {
                        try { colliders[i].enabled = false; }
                        catch (Exception exception) { ModLog.SuppressedException("remote-player.disable-prefab-collider", exception); }
                    }
                    AddPlayerCollider(go);

                    isRealModel = true;
                    ModLog.Debug($"[Ghost] Spawned native Cairn player for {id} ({name})");

                    ApplyInitialTransform(go, rp);

                    Object.DontDestroyOnLoad(go);
                    _ghosts[id] = new GhostEntry
                    {
                        Root = go,
                        NrpComponent = nrpComp,
                        IsRealModel = true,
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

    /// <summary>
    /// Adds a capsule collider to the ghost's root. Acts as a fallback for any Unity
    /// physics that checks colliders, even though Cairn's main movement system
    /// bypasses standard physics.
    /// </summary>
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

    private static void AddPlayerCollider(GameObject ghost)
    {
        try
        {
            var col = ghost.AddComponent<CapsuleCollider>();
            col.center = new Vector3(0f, 0.9f, 0f);
            col.radius = 0.3f;
            col.height = 1.8f;
            col.direction = 1; // Y axis
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[Ghost] AddPlayerCollider failed: {ex.Message}");
        }
    }
}
