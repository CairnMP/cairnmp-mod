using System;
using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Networking;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnMultiplayerMod.Core;

/// <summary>
/// Gestionnaire de fantomes distants. Les objets sont crees des que le joueur
/// distant est connu, puis conserves pendant les chargements et animations.
///
/// Cairn peut bloquer l'apparition si le remote player est instancie trop tard
/// apres les transitions de scene. On privilegie donc un spawn tot, avec un
/// placement hors ecran tant qu'aucune pose exploitable n'a ete recue.
///
/// Essaie d'abord le vrai modele Cairn (MC_Netplay_Player.prefab), puis utilise
/// un humanoide primitif en secours si le prefab n'est pas disponible.
/// </summary>
public static class RemotePlayerManager
{
    // Le prefab natif doit rester actif : NetplayRemotePlayer.Update applique
    // le frame courant sur les renderers apres SetFrame.
    private const bool UseNativePlayerPrefab = true;
    private const float FirstFrameGraceSeconds = 15f;
    private const float FirstFrameWaitLogIntervalSeconds = 3f;
    private const double PlayerFrameFreshSeconds = 0.5;

    /// <summary>
    /// Affichage des plaques de nom au-dessus des fantomes distants. Bascule via la
    /// touche N (utile en mode photo pour des captures propres). Applique chaque frame
    /// dans UpdateAll sur le nameMesh natif de chaque fantome.
    /// </summary>
    public static bool ShowNames { get; private set; } = true;

    /// <summary>Inverse l'affichage des noms des joueurs distants. Renvoie le nouvel etat.</summary>
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

        // Interpolation native appliquee une fois sur le climbot distant (cree paresseusement
        // au premier frame climbot recu) — meme lissage que le joueur.
        public bool ClimbotLerpApplied;

        // Dernier mode applique sur le composant AavaLightStick — sert a
        // dedupliquer les appels SetMode cote ghost.
        public int LastAppliedLampMode;
        public bool HasAppliedLampState;

        // Dernier mode anchor du baton applique (Locator/Default) — dedup, cf. ApplyPendingLampStates.
        public int LastAppliedStickAnchor;
        public bool HasAppliedStickAnchor;

        // Dernier bitfield d'outfit applique (meshes actifs) — dedup, cf. ApplyPendingLampStates.
        public int LastAppliedOutfit;
        public bool HasAppliedOutfit;

        // Dernier etat de gants lumineux applique sur le ghost — dedup des appels.
        public bool LastAppliedGloves;
        public bool HasAppliedCosmeticState;


        // Derniere pose de doigts appliquee (reference du tableau recu) — sert a
        // ne reappliquer que sur changement (les doigts persistent entre frames).
        public byte[] LastAppliedHandPosePacked;

        // Cordes netplay du fantome (NetLogicalRope*, vertes par defaut) a recolorer
        // en blanc. Cache une fois trouvees ; scan throttle tant que null.
        public System.Collections.Generic.List<LineRenderer> NetRopes;
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


    private static readonly Vector3 HiddenSpawnPosition = new(0f, -10000f, 0f);

    private static readonly Color[] _palette =
    {
        new(0.20f, 0.80f, 1.00f), // bleu cyan
        new(1.00f, 0.80f, 0.20f), // jaune ambre
        new(1.00f, 0.30f, 0.70f), // rose
        new(0.40f, 1.00f, 0.40f), // vert clair
        new(1.00f, 0.40f, 0.30f), // corail
        new(0.80f, 0.60f, 1.00f), // violet
    };

    /// <summary>
    /// Couleur stable associee a un id de joueur (meme palette que les fantomes).
    /// Reutilisee par les marqueurs de ping pour identifier l'auteur a la couleur.
    /// </summary>
    public static Color ColorForPlayer(int playerId)
    {
        var index = (playerId % _palette.Length + _palette.Length) % _palette.Length;
        return _palette[index];
    }

    /// <summary>
    /// Appelé chaque frame tant qu'on est connecté. Fait apparaître les fantômes
    /// dès que le réseau connaît un joueur distant et les garde vivants pendant
    /// les transitions. Les fantômes ne disparaissent que si le joueur quitte.
    /// </summary>
    public static void Reconcile(NetworkManager net, PlayerState localState)
    {
        if (net == null) return;

        // Cree les fantomes des que le reseau connait un joueur distant.
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

        // Ne supprime pas pendant Loading/Connecting : Cairn peut bloquer une
        // reapparition tardive. On retire seulement les joueurs vraiment partis.
        var toRemove = new List<int>();
        foreach (var kv in _ghosts)
        {
            if (!net.RemotePlayers.ContainsKey(kv.Key))
                toRemove.Add(kv.Key);
        }
        foreach (var id in toRemove)
        {
            if (_ghosts.TryGetValue(id, out var entry))
            {
                if (entry.Root != null) Object.Destroy(entry.Root);
                _ghosts.Remove(id);
                _spawnWaits.Remove(id);
                Mod.LogDebug($"[Ghost] Despawned player {id} (left session)");
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
            Mod.Log.Warning($"[Ghost] Still waiting before spawning {rp.Id} ({rp.Name}) after {age:0.0}s {reason}");
            return;
        }

        if (now - wait.LastLogAt < FirstFrameWaitLogIntervalSeconds) return;

        wait.LastLogAt = now;
        Mod.LogDebug($"[Ghost] Waiting before spawning {rp.Id} ({rp.Name}) age={age:0.0}s {reason}");
    }

    private static string FormatLastUpdateAge(RemotePlayer rp)
    {
        if (rp == null || rp.LastUpdateTime <= 0) return "never";

        var now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        return $"{Math.Max(0, now - rp.LastUpdateTime):0.0}s ago";
    }

    /// <summary>
    /// Position monde du point d'attache de la corde sur le baudrier d'un fantome
    /// (NetplayRemoteHarness.GetAttachPosition()). Sert a encorder la corde entre
    /// joueurs au vrai baudrier, comme le jeu. False si le fantome / son baudrier
    /// n'est pas disponible.
    /// </summary>
    public static bool TryGetGhostHarnessAttachPosition(int playerId, out Vector3 pos)
    {
        pos = default;
        if (!_ghosts.TryGetValue(playerId, out var entry)) return false;
        if (!entry.IsRealModel || entry.NrpComponent == null) return false;
        try
        {
            var harness = entry.NrpComponent.Harness;
            return harness != null && CairnGameApi.TryGetHarnessAttachPosition(harness, out pos);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Renvoie le baudrier (NetplayRemoteHarness) d'un fantome, pour la sonde belay.</summary>
    public static bool TryGetGhostHarness(int playerId, out Il2Cpp.Harness harness)
    {
        harness = null;
        if (!_ghosts.TryGetValue(playerId, out var entry)) return false;
        if (!entry.IsRealModel || entry.NrpComponent == null) return false;
        try
        {
            harness = entry.NrpComponent.Harness;
            return harness != null;
        }
        catch
        {
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
    /// Met à jour le transform de chaque fantôme à partir des dernières données réseau.
    /// Appelé chaque frame après Reconcile.
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

            // Recolore la corde netplay verte du fantome en blanc.
            if (entry.IsRealModel)
                RecolorGhostNetRopes(entry);

            // Pilote l'animation via le pipeline SetFrame natif du jeu.
            if (entry.IsRealModel && entry.NrpComponent != null && HasFreshPlayerFrame(rp))
            {
                CairnGameApi.CallNetplaySetFrame(entry.NrpComponent, rp.Id, rp.Name, rp.PlayerFrame);

                if (rp.HasClimbotFrame)
                {
                    CairnGameApi.CallNetplayClimbotSetFrame(entry.NrpComponent, rp.Id, rp.ClimbotFrame);

                    // Active l'interpolation native du climbot une fois qu'il existe (cree au
                    // premier frame) — sinon il snappe alors que le joueur est lisse.
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
                        catch { /* cosmetique : ne jamais bloquer la boucle de rendu */ }
                    }
                }

                continue;
            }

            ApplyRootPoseFallback(entry, rp);
        }

        ApplyPendingLampStates(net);
        ApplyPendingCosmetics(net);
        ApplyPendingFingerPoses(net);

        // Repose les rigs de gants des fantomes sur leurs os de corps (apres les SetFrame du frame).
        CairnGameApi.TickGhostGloveRigs();

        // Etat des plaques de nom : applique APRES tous les SetFrame du frame (le natif
        // peut reactiver le nameMesh), pour avoir toujours le dernier mot et eviter le
        // clignotement. N'affecte que les fantomes du vrai modele (champ natif nameMesh).
        foreach (var kv in net.RemotePlayers)
        {
            if (!_ghosts.TryGetValue(kv.Key, out var entry)) continue;
            if (entry.IsRealModel && entry.NrpComponent != null)
                CairnGameApi.SetGhostNameVisible(entry.NrpComponent, ShowNames);
        }
    }

    /// <summary>
    /// Recolore la corde netplay du fantome (LineRenderers nommes NetLogicalRope*, en
    /// vert debug RGBA(0,1,0.016) par defaut) en blanc, pour matcher la corde locale
    /// (LogicalRope). Le scan GetComponentsInChildren est throttle a 2 Hz tant qu'on n'a
    /// rien trouve (la corde peut etre creee tardivement) ; une fois trouvees, on reaffirme
    /// le blanc chaque frame au cas ou le jeu re-verdit la corde (cout negligeable).
    /// </summary>
    private static void RecolorGhostNetRopes(GhostEntry entry)
    {
        if (entry.Root == null) return;

        // Cache invalide (corde detruite/recreee) -> on relance un scan.
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
        catch { }
    }

    /// <summary>
    /// Applique les poses de doigts recues sur les fantomes, apres le pipeline
    /// natif (SetFrame) qui pose le corps. Ne reapplique que sur changement : les
    /// localRotation des doigts persistent entre frames (ils ne sont pas touches
    /// par le set d'os du corps).
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

            if (CairnGameApi.TryApplyRemoteFingerPose(entry.NrpComponent, rp.HandPosePacked))
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

            // L'int lampe transporte le mode lumiere (bits 0-7) + le mode anchor du baton (bits
            // 8-15) + le bitfield d'outfit (bits 16-24, cf. PlayerStateBroadcaster). On decode et
            // applique chacun separement.
            int lightMode = rp.LampMode & 0xFF;
            int anchorMode = (rp.LampMode >> 8) & 0xFF;
            int outfitBits = (rp.LampMode >> 16) & CairnGameApi.OutfitBitsMask;

            if (!entry.HasAppliedLampState || entry.LastAppliedLampMode != lightMode)
            {
                if (CairnGameApi.TryApplyRemoteLampState(entry.NrpComponent, lightMode))
                {
                    entry.LastAppliedLampMode = lightMode;
                    entry.HasAppliedLampState = true;
                }
            }

            if (!entry.HasAppliedStickAnchor || entry.LastAppliedStickAnchor != anchorMode)
            {
                if (CairnGameApi.ApplyGhostStickByAnchorMode(entry.NrpComponent, anchorMode))
                {
                    entry.LastAppliedStickAnchor = anchorMode;
                    entry.HasAppliedStickAnchor = true;
                }
            }

            if (!entry.HasAppliedOutfit || entry.LastAppliedOutfit != outfitBits)
            {
                if (CairnGameApi.ApplyGhostOutfitBits(entry.NrpComponent, outfitBits))
                {
                    entry.LastAppliedOutfit = outfitBits;
                    entry.HasAppliedOutfit = true;
                }
            }
        }
    }

    /// <summary>
    /// Applique l'etat cosmetique recu (gants lumineux pour l'instant) sur les
    /// fantomes. Ne reapplique que sur changement, comme la lampe.
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
                // Mesh des gants (rig clone) + lueur (point-lights synthetiques aux mains, fiables —
                // le clone des vraies lumieres derivait dans le vide a cause de leur script de suivi).
                bool ok = CairnGameApi.SetGhostGloveMesh(entry.NrpComponent, glovesOn);
                CairnGameApi.SetGhostGlowingGloves(entry.NrpComponent, glovesOn);
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

    /// <summary>
    /// Retourne la racine monde du fantome (PlayerFrame.Positions[0..2]) si la frame est
    /// fraiche — exactement la source qui place le CORPS du fantome. Sert a l'encordement
    /// pour que l'ancre de corde suive le meme point que le corps (sinon elle derive d'un
    /// autre flux, ServerPlayerState, et etire la corde). false si pas de frame fraiche.
    /// </summary>
    public static bool TryGetFreshBodyRoot(RemotePlayer rp, out Vector3 root)
    {
        root = default;
        if (rp == null || !HasFreshPlayerFrame(rp)) return false;
        var pos = rp.PlayerFrame.Positions;
        if (pos == null || pos.Length < 3) return false;
        root = new Vector3(pos[0], pos[1], pos[2]);
        return true;
    }

    private static void ApplyRootPoseFallback(GhostEntry entry, RemotePlayer rp)
    {
        entry.Root.transform.position = new Vector3(rp.X, rp.Y, rp.Z);
        entry.Root.transform.rotation = Quaternion.Euler(0f, rp.YawDeg, 0f);

        if (entry.IsRealModel && !entry.RootPoseFallbackLogged)
        {
            entry.RootPoseFallbackLogged = true;
            Mod.Log.Warning($"[Ghost] Using root pose fallback for {rp.Id} ({rp.Name}) while player NetFrame is stale");
        }
    }

    /// <summary>Détruit tous les fantômes — appelé à la déconnexion.</summary>
    public static void ClearAll()
    {
        foreach (var kv in _ghosts)
            if (kv.Value?.Root != null) Object.Destroy(kv.Value.Root);
        _ghosts.Clear();
        _spawnWaits.Clear();
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
            catch
            {
                // Le ghost peut etre en cours de destruction pendant un changement de scene.
            }
        }

        return false;
    }

    // -- Callbacks de suivi (pas de travail visuel) ---------------------------

    public static void OnPlayerJoined(int id, string name, RemotePlayer rp = null)
    {
        Mod.LogDebug($"[Ghost] Player {id} ({name}) joined -- waiting for first NetFrame");
    }

    public static void OnPlayerLeft(int id)
    {
        if (_ghosts.TryGetValue(id, out var entry))
        {
            if (entry.Root != null) Object.Destroy(entry.Root);
            _ghosts.Remove(id);
            Mod.LogDebug($"[Ghost] Despawned player {id}");
        }
        _spawnWaits.Remove(id);
    }

    // -- Interne ---------------------------------------------------------

    private static void SpawnGhost(int id, string name, RemotePlayer rp)
    {
        if (_ghosts.ContainsKey(id)) return;
        GameObject go = null;
        try
        {
            // Essaie d'abord le vrai prefab Cairn. En cas d'échec ou s'il est invisible,
            // se rabat sur l'humanoïde primitif.
            var prefab = UseNativePlayerPrefab
                ? CairnGameApi.TryGetNetplayClimberPrefab()
                : null;

            bool isRealModel = false;

            if (prefab != null)
            {
                go = Object.Instantiate(prefab);
                go.name = $"MP_Ghost_{id}_{name}";

                // Garde les behaviours natifs actifs : leur Update applique le
                // frame courant sur le mesh apres nos appels SetFrame.
                var nrpComp = go.GetComponent<NetplayRemotePlayer>()
                    ?? go.GetComponentInChildren<NetplayRemotePlayer>(true);

                // Active les visuels attendus par le prefab natif.
                if (nrpComp == null)
                {
                    Mod.Log.Warning("[Ghost] NetplayRemotePlayer component missing from native prefab");
                    Object.Destroy(go);
                    go = null;
                }

                if (go != null)
                {
                go.SetActive(true);
                ActivateNativeRemoteVisuals(go);

                // Interpolation native : le prefab lisse le mouvement vers currentFrame dans
                // son Update(). On l'active explicitement (le mod ecrit currentFrame en inline
                // a la frequence reseau ; sans lerp le rendu snap a chaque paquet). On preserve
                // un lerpSpeed deja configure, sinon repli raisonnable. Log des defauts pour reglage.
                if (nrpComp != null)
                {
                    try
                    {
                        Mod.LogDebug($"[Ghost] native lerp defaults: lerp={nrpComp.lerp} lerpSpeed={nrpComp.lerpSpeed}");
                        nrpComp.lerp = true;
                        if (nrpComp.lerpSpeed <= 0f) nrpComp.lerpSpeed = 12f;
                    }
                    catch { /* le lerp est cosmetique : ne jamais bloquer le spawn */ }

                    // Skin natif : teinte chaque joueur distant avec la palette officielle du
                    // jeu (NetplayPlayerSkin.SetColorIndex) au lieu de la palette manuelle du
                    // mod. Index stable par joueur (modulo conservateur pour rester en plage).
                    try
                    {
                        var skin = nrpComp.skin != null
                            ? nrpComp.skin
                            : go.GetComponentInChildren<NetplayPlayerSkin>(true);
                        if (skin != null)
                        {
                            byte colorIndex = (byte)(((id % 6) + 6) % 6);
                            skin.SetColorIndex(colorIndex);
                            Mod.LogDebug($"[Ghost] native skin color index {colorIndex} applied to {id}");
                        }
                    }
                    catch { /* le skin est cosmetique : ne jamais bloquer le spawn */ }
                }

                // Force updateWhenOffscreen sur les SkinnedMesh + active les renderers.
                var skinned = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int i = 0; i < skinned.Count; i++)
                {
                    var r = skinned[i];
                    if (r == null) continue;
                    try { r.enabled = true; r.updateWhenOffscreen = true; } catch { }
                }

                // Supprime les caméras.
                var cameras = go.GetComponentsInChildren<Camera>(true);
                for (int i = 0; i < cameras.Count; i++) Object.Destroy(cameras[i]);

                // Désactive les colliders du prefab, ajoute notre capsule collider.
                var colliders = go.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Count; i++) try { colliders[i].enabled = false; } catch { }
                AddPlayerCollider(go);

                isRealModel = true;
                Mod.LogDebug($"[Ghost] Spawned native Cairn player for {id} ({name})");

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
                Mod.LogDebug($"[Ghost] Fallback primitive humanoid for player {id} ({name})");
            }
        }
        catch (System.Exception ex)
        {
            Mod.Log.Error($"[Ghost] Spawn failed for {id}: {ex.Message}");
            if (go != null) Object.Destroy(go);
        }
    }

    /// <summary>
    /// Ajoute un capsule collider à la racine du fantôme. Sert de secours pour
    /// toute physique Unity qui vérifie les colliders, même si le système de
    /// mouvement principal de Cairn contourne la physique standard.
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
        catch (System.Exception ex)
        {
            Mod.Log.Warning($"[Ghost] Could not activate child '{childName}': {ex.Message}");
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
            col.direction = 1; // axe Y
        }
        catch (System.Exception ex)
        {
            Mod.Log.Warning($"[Ghost] AddPlayerCollider failed: {ex.Message}");
        }
    }
}
