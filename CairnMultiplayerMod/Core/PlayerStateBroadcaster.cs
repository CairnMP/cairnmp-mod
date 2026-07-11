using CairnMultiplayer.Shared;
using UnityEngine;

namespace CairnMultiplayerMod.Core;

public partial class Mod
{
    private float _timeSinceLastSceneLoad;
    private float _lampPollTimer;
    private bool _hasLastSentLampState;
    private int _lastSentLampMode;
    private float _cosmeticPollTimer;
    private bool _hasLastSentCosmetic;
    private byte _lastSentCosmeticFlags;
    private float _handPosePollTimer;
    private byte[] _lastSentHandPosePacked;
    private const float NetFrameMissingLogIntervalSeconds = 3f;
    private bool _debugLoggedFirstPlayerFrameCapture;
    private bool _debugLoggedFirstClimbotFrameCapture;
    private float _debugLastMissingPlayerFrameLogAt;
    private float _debugLastMissingClimbotFrameLogAt;

    /// <summary>
    /// Gère la diffusion périodique de l'état, les vérifications de pitons
    /// et le cycle de vie des fantômes. Appelé chaque frame depuis OnUpdate().
    /// </summary>
    private void TickPlayerSync()
    {
        // Diffusion périodique de l'état du joueur local + synchronisation des fantômes distants.
        if (_network.IsHandshakeComplete)
        {
            if (IsGameplaySyncSuspended())
            {
                TickSuspendedNetworkPresence();
                return;
            }

            _stateTickTimer += Time.unscaledDeltaTime;
            if (_stateTickTimer >= Protocol.PlayerStateUpdateIntervalSeconds)
            {
                _stateTickTimer = 0f;
                SendLocalPlayerState();
            }

            _boneTickTimer += Time.unscaledDeltaTime;
            if (_boneTickTimer >= Protocol.BoneStateUpdateIntervalSeconds)
            {
                _boneTickTimer = 0f;
                SendLocalNetFrames();
            }

            TickWeatherSync();
            TickLampSync();
            TickCosmeticSync();
            TickHandPoseSync();

            // Vérifie les nouveaux placements de pitons (une fréquence plus basse suffit).
            if (LocalState == PlayerState.InGame)
            {
                if (CairnGameApi.CheckForNewPiton(out var pitonId, out var pitonPos,
                    out var pitonRot, out var pitonQuality, out var pitonHp, out var pitonItemId))
                {
                    _network.SendPitonPlaced(pitonId, pitonPos, pitonRot, pitonQuality, pitonHp, pitonItemId);
                }

                if (CairnGameApi.CheckForRemovedPiton(out var removedPitonId))
                {
                    _network.SendPitonRemoved(removedPitonId);
                }
            }

            // Cycle de vie des fantomes : spawn tot, conservation pendant les
            // transitions, puis pilotage des transforms par les derniers paquets.
            RemotePlayerManager.Reconcile(_network, LocalState);
            RemotePlayerManager.UpdateAll(_network, LocalState);
        }
        else
        {
            // Déconnecté — supprime les fantômes résiduels.
            if (LocalState == PlayerState.Unknown)
                RemotePlayerManager.ClearAll();
        }
    }

    /// <summary>
    /// Garde une presence reseau minimale pendant le bivouac sans toucher au
    /// graphe gameplay original (pas de MC, NetFrame, meteo, pitons, fantomes).
    /// </summary>
    private void TickSuspendedNetworkPresence()
    {
        _boneTickTimer = 0f;
        _weatherTickTimer = Protocol.WeatherStateUpdateIntervalSeconds;

        _stateTickTimer += Time.unscaledDeltaTime;
        if (_stateTickTimer < Protocol.PlayerStateUpdateIntervalSeconds)
            return;

        _stateTickTimer = 0f;
        _network.SendPlayerState(0f, 0f, 0f, 0f, CurrentNetworkSceneName(), PlayerState.Loading);
    }

    /// <summary>
    /// Envoie la position du corps du joueur local + l'état de cycle de vie actuel.
    /// Préfère le vrai transform du MC via PawnManager.MCGameObject ; se rabat
    /// sur Camera.main tant que le MC n'a pas encore été instancié.
    /// </summary>
    private void SendLocalPlayerState()
    {
        if (LocalState != PlayerState.InGame)
        {
            _network.SendPlayerState(0f, 0f, 0f, 0f, CurrentNetworkSceneName(), LocalState);
            return;
        }

        Vector3 p; float yaw;
        if (!CairnGameApi.TryGetLocalPlayerPose(out p, out yaw))
        {
            var cam = Camera.main;
            if (cam == null) return;
            var t = cam.transform;
            p = t.position;
            yaw = t.eulerAngles.y;
        }
        _network.SendPlayerState(p.x, p.y, p.z, yaw, CurrentNetworkSceneName(), LocalState);
    }

    /// <summary>
    /// Détermine l'état de cycle de vie actuel à partir des signaux
    /// (connexion, handshake, scène, apparition du MC, stabilité de scène).
    /// </summary>
    /// <remarks>
    /// L'état `InGame` est conditionné par ~1 seconde de stabilité de scène après
    /// la résolution du MC. C'est la règle critique : on ne fait jamais apparaître ni
    /// disparaître de fantômes pendant les transitions de scène, car c'est là que le
    /// pipeline Addressables de Cairn plante quand on touche aux instances de
    /// NetplayClimberPrefab.
    /// </remarks>
    private PlayerState ComputeLocalState()
    {
        if (!_network.IsConnected)
            return PlayerState.Unknown;
        if (!_network.IsHandshakeComplete)
            return PlayerState.Connecting;
        if (_currentScene == null)
            return PlayerState.Connecting;

        // Catégorie menu principal — couvre "MainMenu" et "MainMenuBackgroundsBase".
        if (_currentScene.StartsWith("MainMenu"))
            return PlayerState.InMenu;

        if (_currentScene == "LoadingScreen" || _currentScene == "CommonBaseScene")
            return PlayerState.Loading;

        if (CairnGameApi.TryGetGameLifecycle(out var lifecycle, out _))
        {
            if (lifecycle == CairnGameLifecycleState.Menu)
                return PlayerState.InMenu;

            if (lifecycle != CairnGameLifecycleState.InGame)
                return PlayerState.Loading;
        }
        else if (IsNonGameplayScene(_currentScene))
        {
            return PlayerState.Loading;
        }

        // Dans une scene de gameplay, on attend que le graphe soit stable avant
        // d'autoriser les captures natives et les fantomes. Apres une mort, une
        // camera peut etre active avant que le nouveau MC existe ; utiliser cette
        // camera comme signal InGame declenche des captures sur des objets detruits.
        if (_timeSinceLastSceneLoad < 1.0f)
            return PlayerState.Loading;

        if (CairnGameApi.TryGetLocalPlayerPose(out _, out _))
            return PlayerState.InGame;

        return PlayerState.Loading;
    }

    /// <summary>
    /// Capture et envoie les frames Netplay natives locales au rythme animation.
    /// </summary>
    private void SendLocalNetFrames()
    {
        if (LocalState != PlayerState.InGame) return;

        if (CairnGameApi.TryCaptureLocalPlayerFrame(out var playerFrame))
        {
            if (!_debugLoggedFirstPlayerFrameCapture)
            {
                _debugLoggedFirstPlayerFrameCapture = true;
                Mod.LogDebug($"[NetSync] First local player NetFrame captured positions={FrameVectorCount(playerFrame.Positions)} eulers={FrameVectorCount(playerFrame.Eulers)} flags=0x{playerFrame.Flags:X2}");
            }
            _network.SendPlayerFrame(playerFrame);
        }
        else
        {
            LogMissingLocalNetFrame("player", ref _debugLastMissingPlayerFrameLogAt);
        }

        if (CairnGameApi.TryCaptureLocalClimbotFrame(out var climbotFrame))
        {
            if (!_debugLoggedFirstClimbotFrameCapture)
            {
                _debugLoggedFirstClimbotFrameCapture = true;
                Mod.LogDebug($"[NetSync] First local climbot NetFrame captured positions={FrameVectorCount(climbotFrame.Positions)} eulers={FrameVectorCount(climbotFrame.Eulers)} flags=0x{climbotFrame.Flags:X2}");
            }
            _network.SendClimbotFrame(climbotFrame);
        }
        else
        {
            LogMissingLocalNetFrame("climbot", ref _debugLastMissingClimbotFrameLogAt);
        }
    }

    private static int FrameVectorCount(float[] values) => values == null ? 0 : values.Length / 3;

    private static bool IsNonGameplayScene(string sceneName)
    {
        return sceneName == "BivouacIndoor";
    }

    private string CurrentNetworkSceneName()
    {
        if (IsNonGameplayScene(_currentScene) && !string.IsNullOrEmpty(_lastGameplayScene))
            return _lastGameplayScene;

        return _currentScene ?? "";
    }

    private void ResetPlayerSyncDebugState()
    {
        _debugLoggedFirstPlayerFrameCapture = false;
        _debugLoggedFirstClimbotFrameCapture = false;
        _debugLastMissingPlayerFrameLogAt = 0f;
        _debugLastMissingClimbotFrameLogAt = 0f;
        _lampPollTimer = 0f;
        _hasLastSentLampState = false;
        _lastSentLampMode = 0;
        _cosmeticPollTimer = 0f;
        _hasLastSentCosmetic = false;
        _lastSentCosmeticFlags = 0;
        CairnGameApi.ResetLocalCosmeticsCache();
        _handPosePollTimer = 0f;
        _lastSentHandPosePacked = null;
        CairnGameApi.ResetLocalLampStateCache();
        // Pas de reset de la detection freecam ici : l'etat eagle-eye/Display Route
        // est pilote par les events natifs et persiste a travers le streaming de
        // scene. Le reinitialiser ferait croire au mod qu'on est sorti du Display
        // Route (alors qu'on y est toujours) -> impossible de poser un ping tant
        // qu'on n'a pas re-toggle.
        CairnGameApi.ResetFingerSyncCache();
        ResetTimeSyncState();
        ResetRopeCoupleState();
    }

    /// <summary>
    /// Sonde l'etat de la lampe locale et broadcast aux autres joueurs des qu'il
    /// change. Pas de message tant qu'on n'a pas pu lire au moins une fois pour
    /// eviter d'envoyer un "off" trompeur pendant les chargements.
    /// </summary>
    private void TickLampSync()
    {
        if (LocalState != PlayerState.InGame) return;

        _lampPollTimer += Time.unscaledDeltaTime;
        if (_lampPollTimer < Protocol.LampStatePollIntervalSeconds)
            return;
        _lampPollTimer = 0f;

        if (!CairnGameApi.TryGetLocalLampState(out var lightMode))
            return;

        // L'int lampe transporte aussi (bits hauts, sans nouveau paquet) :
        //  - bits 8-15  : mode de l'anchor du baton (Locator/Default) -> ApplyGhostStickByAnchorMode
        //  - bits 16-24 : bitfield d'outfit (meshes actifs) -> ApplyGhostOutfitBits
        int anchorMode = 0;
        CairnGameApi.TryGetLocalStickAnchorMode(out anchorMode);
        int outfitBits = CairnGameApi.GetLocalOutfitBits();
        int packed = (lightMode & 0xFF) | ((anchorMode & 0xFF) << 8)
                     | ((outfitBits & CairnGameApi.OutfitBitsMask) << 16);

        if (_hasLastSentLampState && _lastSentLampMode == packed)
            return;

        _lastSentLampMode = packed;
        _hasLastSentLampState = true;
        _network.SendLampState(packed);
    }

    /// <summary>
    /// Sonde l'etat cosmetique local (gants lumineux pour l'instant) et le broadcast
    /// des qu'il change. Meme logique que la lampe : pas d'envoi tant qu'on n'a pas pu
    /// lire au moins une fois, et uniquement sur changement (cosmetiques quasi statiques).
    /// </summary>
    private void TickCosmeticSync()
    {
        if (LocalState != PlayerState.InGame) return;

        _cosmeticPollTimer += Time.unscaledDeltaTime;
        if (_cosmeticPollTimer < Protocol.CosmeticStatePollIntervalSeconds)
            return;
        _cosmeticPollTimer = 0f;

        if (!CairnGameApi.TryGetLocalCosmetics(out var flags))
            return;

        if (_hasLastSentCosmetic && _lastSentCosmeticFlags == flags)
            return;

        _lastSentCosmeticFlags = flags;
        _hasLastSentCosmetic = true;
        _network.SendCosmeticState(flags);
    }

    /// <summary>
    /// Capture la pose des doigts locale et la diffuse a ~12 Hz, uniquement quand
    /// elle change (les doigts immobiles ne generent aucun trafic).
    /// </summary>
    private void TickHandPoseSync()
    {
        if (LocalState != PlayerState.InGame) return;

        _handPosePollTimer += Time.unscaledDeltaTime;
        if (_handPosePollTimer < Protocol.HandPosePollIntervalSeconds)
            return;
        _handPosePollTimer = 0f;

        if (!CairnGameApi.TryCaptureLocalFingerPose(out var packed))
            return;

        if (BytesEqual(_lastSentHandPosePacked, packed))
            return;

        _lastSentHandPosePacked = packed;
        _network.SendHandPose(packed);
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static void LogMissingLocalNetFrame(string target, ref float lastLogAt)
    {
        var now = Time.unscaledTime;
        if (now - lastLogAt < NetFrameMissingLogIntervalSeconds) return;

        lastLogAt = now;
        Mod.LogDebug($"[NetSync] Waiting for local {target} NetFrame capture while InGame");
    }
}
