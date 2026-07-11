using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CairnMultiplayer.Shared;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using CairnMultiplayerMod.Networking;
using CairnMultiplayerMod.UI;

[assembly: MelonInfo(typeof(CairnMultiplayerMod.Core.Mod), "Cairn Multiplayer Mod", "1.0.0", "CairnModTeam")]
[assembly: MelonGame("TheGameBakers", "Cairn")]

namespace CairnMultiplayerMod.Core;

public partial class Mod : MelonMod
{
    public static Mod Instance { get; private set; }
    public static MelonLogger.Instance Log => Instance.LoggerInstance;

    /// <summary>Logs de diagnostic verbeux (scenes, resolution d'objets natifs, etc.). OFF par
    /// defaut pour garder la console propre ; passer a true pour deboguer.</summary>
    public static bool VerboseLogging;

    /// <summary>Log de diagnostic : n'ecrit QUE si VerboseLogging est actif.</summary>
    public static void LogDebug(string message)
    {
        if (VerboseLogging) Log.Msg(message);
    }

    private NetworkManager _network;
    private SteamLobbyManager _lobby;
    private IMultiplayerPanel _panel;
    private Chat.ChatController _chat;
    private string _currentScene;
    private readonly HashSet<string> _loadedScenes = new(StringComparer.Ordinal);
    private string _lastGameplayScene;
    private Key _connectKey;
    private Key _disconnectKey;
    private float _stateTickTimer;
    private float _boneTickTimer;
    private bool _gameplaySyncSuspended;
    private bool _netplaySetFramePatchPausedForBivouac;
    // Reprise DIFFEREE du patch SetFrame apres la sortie de bivouac : le scellage /
    // l'ecriture disque du package de save natif peut se terminer quelques instants
    // APRES que le flag bivouac retombe. Reactiver l'injection SetFrame pile dans
    // cette fenetre laissait le package dispose -> 1 save OK puis plus rien. On garde
    // donc le patch en pause encore quelques secondes (0 = aucune reprise programmee).
    private float _setFramePatchResumeAt;
    private const float SetFramePatchResumeGraceSeconds = 3f;
    private float _nextBivouacDebugLogAt;
    private float _bivouacSuspendedSince;
    private const float BivouacDebugLogIntervalSeconds = 3f;
    // Diagnostic : fenetre de surveillance apres la sortie de bivouac pour
    // verifier si les fantomes reapparaissent (cas du deadlock a double-gate).
    private float _bivouacRecoveryWatchUntil;
    private float _nextBivouacRecoveryLogAt;
    private const float BivouacRecoveryWatchSeconds = 30f;
    private const float BivouacRecoveryLogIntervalSeconds = 3f;
    // Garde-fou anti-blocage du bivouac : si le flag natif reste coince a la
    // sortie, on force la reprise de synchro pour eviter un desync permanent.
    private UnityEngine.Vector3 _bivouacSuspendPawnPos;
    private bool _hasBivouacSuspendPawnPos;
    private float _bivouacStuckSince;
    private const float BivouacStuckResumeSeconds = 8f;
    private const float BivouacStuckMoveThresholdSqr = 2.25f; // ~1.5 m de deplacement

    // Suivi de la progression de la création/join de lobby pour afficher un
    // statut qui évolue pendant l'attente (auto-provision Hetzner = 1-3 min).
    private DateTime? _connectingStart;
    private string    _connectingVerb = "Creating lobby"; // "Creating lobby" | "Joining lobby"
    private string    _lastLobbyError;

    // Queue d'actions à exécuter sur le thread Unity — les callbacks async
    // (ContinueWith) tournent sur ThreadPool et ne peuvent PAS toucher
    // directement les objets Unity (TMP, Image, ...) en IL2CPP.
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();

    /// <summary>Schedule une action à exécuter au prochain tick d'Update sur le thread Unity.</summary>
    private void RunOnMainThread(Action action) => _mainThreadActions.Enqueue(action);

    public NetworkManager Network => _network;
    public SteamLobbyManager Lobby => _lobby;
    public PlayerState LocalState { get; private set; } = PlayerState.Unknown;

    public override void OnInitializeMelon()
    {
        Instance = this;
        CrashReporter.Init();
        Il2CppExceptionCapture.Install();
        ModConfig.Register();
        CairnGameApi.InstallNetplaySetFramePatch();
        CairnGameApi.InstallBivouacDiagnosticsPatches();
        CairnGameApi.InstallRopeTeamFallPatch();
        CairnGameApi.InstallMultiplayerPausePatch();
        CairnGameApi.InstallFreeRoamUnlockPatch();
        CairnGameApi.InstallSavegamePitonGuardPatch();

        _connectKey = ParseKey(ModConfig.ConnectKey.Value, Key.F5);
        _disconnectKey = ParseKey(ModConfig.DisconnectKey.Value, Key.F6);
        _network = new NetworkManager();
        _lobby   = new SteamLobbyManager();
        _panel = MultiplayerPanelFactory.Create();

        // Chat in-game + commandes admin. Le router contrôle le rôle hôte au dispatch ;
        // les feedbacks de commande s'affichent comme lignes système locales.
        var commandRouter = new Chat.CommandRouter(_network, () => _lobby?.IsHost == true,
            line => _chat?.AddSystemLine(line));
        // canChat exige aussi que le jeu ne soit PAS en pause (Cairn met en pause via
        // timeScale=0). Sinon, ouvrir le chat puis pauser laisserait l'overlay ouvert a
        // forcer le gel d'input -> joueur bloque apres dé-pause. Ici, en pause le chat
        // s'auto-ferme (ChatController.Update) et restaure l'input.
        _chat = new Chat.ChatController(_network, commandRouter,
            () => LocalState == PlayerState.InGame && Time.timeScale > 0f);

        // Câblage Steam Matchmaking → UI. Les callbacks Steam sont pompés par
        // Cairn lui-même sur le thread Unity, donc pas de marshalling à faire.
        _lobby.OnLobbyEntered += id =>
        {
            _connectingStart = null;
            _lastLobbyError = null;
            var code = _lobby.CurrentRoomCode;
            _panel.SetCurrentLobbyName(_lobby.CurrentLobbyName);
            _panel.SetStatus(string.IsNullOrEmpty(code) ? "Connected" : $"Connected — code {code}", true);
            _network.StartSteamTransport(_lobby);
        };
        _lobby.OnLobbyError += err =>
        {
            _connectingStart = null;
            _lastLobbyError = err;
            _panel.SetStatus($"Failed: {err}", _lobby != null && _lobby.IsInLobby);
        };
        _lobby.OnLobbyLeft += () =>
        {
            _network.Disconnect();
            CairnGameApi.ResetRemoteWeatherSyncState();
            RemotePlayerManager.ClearAll();
            PingMarkerManager.ClearAll();
            ClearRopeLinks();
            _gameplaySyncSuspended = false;
            ResumeNetplaySetFramePatchAfterBivouac();
            _panel.SetStatus("Disconnected", false);
        };
        _lobby.OnMembersChanged += () =>
        {
            // Le ConnectedScreen rebuild sa liste à chaque Tick depuis _lobby.Members,
            // mais on push aussi un statut court pour que le footer reflète l'event.
            if (!_lobby.IsInLobby) return;
            _network.RefreshSteamLobbyMembers(_lobby);
            _panel.SetStatus($"{_lobby.Members.Count} player(s) in lobby", true);
        };
        _lobby.OnStartRequested += BeginStartGame;

        // Bouton du menu : le clic Multiplayer cache le menu Cairn et ouvre le panel.
        MainMenuMultiplayerButton.Bind(_panel);

        _panel.OnHostRequested            += OnHostRequested;
        _panel.OnJoinByCodeRequested      += OnJoinByCodeRequested;
        _panel.OnBrowseRequested          += OnBrowseRequested;
        _panel.OnJoinByLobbyIdRequested   += OnJoinByLobbyIdRequested;
        _panel.OnDisconnectRequested      += OnDisconnectRequested;
        _panel.OnStartRequested           += OnStartRequested;
        _panel.OnPanelClosed              += MainMenuMultiplayerButton.RestoreModeSelect;

        _network.OnHandshakeAck += () =>
            _panel.SetStatus($"Connected to {_network.ServerName} (id={_network.LocalPlayerId})", true);
        _network.OnHandshakeRejected += reason =>
            _panel.SetStatus($"Rejected: {reason}", false);
        _network.OnDisconnected += _ =>
        {
            CairnGameApi.ResetRemoteWeatherSyncState();
            ClearRopeLinks();
            _gameplaySyncSuspended = false;
            ResumeNetplaySetFramePatchAfterBivouac();
        };
        _network.OnPlayerJoined += (id, name) =>
        {
            _panel.SetStatus($"Player joined: {name}", _network.IsConnected);
            _network.RemotePlayers.TryGetValue(id, out var rp);
            RemotePlayerManager.OnPlayerJoined(id, name, rp);
        };
        _network.OnPlayerLeft += id =>
        {
            _panel.SetStatus($"Player {id} left", _network.IsConnected);
            RemotePlayerManager.OnPlayerLeft(id);
            RopeLinkState.RemovePlayer(id);
        };
        // Encordement : applique chaque clip/decordage autoritaire a l'etat global des liens.
        _network.OnRopeClip += (from, target, clip) => RopeLinkState.Apply(from, target, clip);

        // Synchronisation des pitons : fait apparaître les pitons placés par les autres joueurs via Lifeline.AddPiton.
        _network.OnPitonPlaced += pkt =>
        {
            if (IsGameplaySyncSuspended())
                return;

            CairnGameApi.SpawnRemotePiton(pkt.PitonId,
                new UnityEngine.Vector3(pkt.PosX, pkt.PosY, pkt.PosZ),
                new UnityEngine.Quaternion(pkt.RotX, pkt.RotY, pkt.RotZ, pkt.RotW),
                pkt.Quality, pkt.PitonHp, pkt.ItemId);
        };
        _network.OnPitonRemoved += pkt =>
        {
            if (IsGameplaySyncSuspended())
                return;

            CairnGameApi.RemoveRemotePiton(pkt.PitonId);
        };
        _network.OnWeatherState += pkt =>
        {
            if (_lobby?.IsHost == true)
                return;
            if (IsGameplaySyncSuspended())
                return;

            CairnGameApi.ApplyRemoteWeather(pkt.State);
        };

        // Marqueur de ping pose par un autre joueur : affiche un waypoint HUD colore.
        _network.OnPingPlaced += pkt =>
            PingMarkerManager.Spawn(pkt.FromPlayerId, new UnityEngine.Vector3(pkt.PosX, pkt.PosY, pkt.PosZ));

        // Heure du jour autoritaire recue de l'hote : appliquee chaque frame cote client.
        _network.OnTimeState += pkt =>
        {
            _remoteTimeState = pkt;
            _hasRemoteTimeState = true;
        };

        // Lancement de partie autoritaire par le serveur : met le paquet en file d'attente,
        // l'applique dans Update quand la scène MainMenu est active.
        _network.OnStartGameReceived += pkt =>
        {
            BeginStartGame(pkt);
        };

        LoggerInstance.Msg("===========================================");
        LoggerInstance.Msg($"  Cairn Multiplayer Mod v{Protocol.GameVersion} loaded!");
        LoggerInstance.Msg($"  Keybinds: {_connectKey} (panel) / {_disconnectKey} (disconnect) / N (toggle player names)");
        LoggerInstance.Msg("===========================================");
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        _loadedScenes.Add(sceneName);
        if (IsGameplayRootScene(sceneName))
            _lastGameplayScene = sceneName;

        _currentScene = sceneName;
        _timeSinceLastSceneLoad = 0f;
        LogDebug($"Scene loaded: [{buildIndex}] {sceneName}");

        // Deblocage FreeRoam : actif UNIQUEMENT au MainMenu (forcer le flag pendant le boot
        // ou en jeu envoie le jeu sur un chemin d'init FreeRoam pas pret -> ecran noir).
        CairnGameApi.SetFreeRoamUnlockActive(sceneName == "MainMenu");

        if (sceneName != "MainMenu")
            _panel.DestroyResources();

        if (IsNonGameplayScene(sceneName))
        {
            LogDebug($"[State] Scene {sceneName} treated as Loading for multiplayer");
            LogBivouacDebug("scene-loaded");
        }
        if (IsSceneBoundCacheResetPoint(sceneName))
        {
            ResetSceneBoundSyncState();
            // Note : on ne vide PAS les pings ici — ils sont positionnes dans le
            // monde et expirent seuls (15 s). Les effacer a chaque streaming de
            // scene les ferait disparaitre alors qu'on est toujours dans la zone.
            if (ShouldClearRemotePlayersOnSceneLoad(sceneName))
                RemotePlayerManager.ClearAll();
        }
        MainMenuMultiplayerButton.OnSceneLoaded(sceneName);
        // Déconnexion automatique lors du retour au MainMenu depuis le gameplay.
        // Nettoie les fantômes et permet au joueur de se reconnecter proprement.
        if (sceneName == "MainMenu" && _network.IsConnected && (_lobby == null || !_lobby.IsInLobby))
        {
            LoggerInstance.Msg("[State] Returned to MainMenu — auto-disconnecting");
            _network.Disconnect();
            RemotePlayerManager.ClearAll();
            ClearRopeLinks();
            _panel.SetStatus("Disconnected (returned to menu)", false);
        }
        else if (sceneName == "MainMenu" && _lobby != null && _lobby.IsInLobby)
        {
            RemotePlayerManager.ClearAll();
            ClearRopeLinks();
        }
    }

    public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
    {
        _loadedScenes.Remove(sceneName);

        var wasCurrentScene = string.Equals(_currentScene, sceneName, StringComparison.Ordinal);
        if (wasCurrentScene)
            _currentScene = ResolveCurrentSceneAfterUnload(sceneName);

        if (!IsNonGameplayScene(sceneName))
            return;

        _timeSinceLastSceneLoad = 0f;
        LogDebug($"Scene unloaded: [{buildIndex}] {sceneName}");
        LogDebug($"[State] Scene {sceneName} overlay unloaded for multiplayer");
        ResetSceneBoundSyncState();
    }

    private static bool IsSceneBoundCacheResetPoint(string sceneName)
    {
        return sceneName == "LoadingScreen"
            || sceneName == "CommonBaseScene"
            || sceneName == "MainMenu"
            || IsGameplayRootScene(sceneName);
    }

    private static bool ShouldClearRemotePlayersOnSceneLoad(string sceneName)
    {
        return sceneName == "LoadingScreen"
            || sceneName == "CommonBaseScene"
            || sceneName == "MainMenu"
            || IsGameplayRootScene(sceneName);
    }

    private void ResetSceneBoundSyncState()
    {
        CairnGameApi.ResetSceneCaches();
        ResetPlayerSyncDebugState();
        _stateTickTimer = 0f;
        _boneTickTimer = 0f;
        _weatherTickTimer = Protocol.WeatherStateUpdateIntervalSeconds;
    }

    private bool IsGameplaySyncSuspended()
    {
        return _network?.IsConnected == true && (_gameplaySyncSuspended || ShouldSuspendGameplaySync());
    }

    private void UpdateGameplaySyncSuspension()
    {
        if (_network == null || !_network.IsConnected)
        {
            if (_gameplaySyncSuspended)
                LogBivouacDebug("network-disconnected");

            _gameplaySyncSuspended = false;
            ResumeNetplaySetFramePatchAfterBivouac();
            return;
        }

        var shouldSuspend = ShouldSuspendGameplaySync();
        if (shouldSuspend == _gameplaySyncSuspended)
            return;

        _gameplaySyncSuspended = shouldSuspend;
        if (_gameplaySyncSuspended)
        {
            LoggerInstance.Msg("[State] Gameplay sync suspended for bivouac");
            _bivouacSuspendedSince = Time.unscaledTime;
            _nextBivouacDebugLogAt = 0f;
            PauseNetplaySetFramePatchForBivouac();
            ResetGameplaySyncTimers();
            CairnGameApi.ResetRemoteWeatherSyncState();
            RemotePlayerManager.ClearAll();
            // Relache les ancres natives de cordee : ClearAll detruit les fantomes, donc
            // une corde restee pinnee sur leur baudrier pointerait dans le vide. On GARDE le
            // lien logique (RopeLinkState) — il sera re-ancre a la sortie quand le fantome du
            // partenaire revient. TickRopeCouple ne tourne pas en bivouac, d'ou ce relachement ici.
            CairnGameApi.ReleaseAllRopeTeamAnchors();
            SetLocalState(PlayerState.Loading);
            // Memorise la position du pawn a l'entree : sert au garde-fou
            // anti-blocage (un pawn qui s'est deplace = on grimpe a nouveau).
            _hasBivouacSuspendPawnPos = CairnGameApi.TryGetLocalPlayerPose(out _bivouacSuspendPawnPos, out _);
            _bivouacStuckSince = 0f;
            LogBivouacDebug("enter");
            return;
        }

        LogBivouacDebug("exit");
        // Arme la surveillance de recuperation : on veut voir, sur chaque client,
        // si les fantomes distants reviennent apres la sortie de bivouac ou si on
        // reste bloque (un cote jamais InGame, ou deadlock mutuel a Loading).
        _bivouacRecoveryWatchUntil = Time.unscaledTime + BivouacRecoveryWatchSeconds;
        _nextBivouacRecoveryLogAt = 0f;
        _hasBivouacSuspendPawnPos = false;
        _bivouacStuckSince = 0f;
        ResumeNetplaySetFramePatchAfterBivouac(immediate: false);
        LoggerInstance.Msg("[State] Gameplay sync resumed");
        ResetSceneBoundSyncState();
    }

    private bool ShouldSuspendGameplaySync()
    {
        if (IsNonGameplayScene(_currentScene))
        {
            _bivouacStuckSince = 0f;
            return true;
        }

        if (!CairnGameApi.TryGetGameLifecycle(out var lifecycle, out _))
        {
            _bivouacStuckSince = 0f;
            return false;
        }

        if (lifecycle != CairnGameLifecycleState.Bivouac)
        {
            _bivouacStuckSince = 0f;
            return false;
        }

        // lifecycle == Bivouac, deduit du flag BivouacManager. Garde-fou contre
        // un flag reste coince a la sortie (cause du desync permanent rapporte) :
        // si GlobalGameManager se declare deja InGame, qu'on est sur une scene de
        // gameplay et que le pawn s'est deplace depuis l'entree (on regrimpe), le
        // flag est perime. On exige ~8 s de persistance pour ne pas confondre avec
        // une vraie transition d'entree/sortie de bivouac.
        if (IsBivouacFlagLikelyStuck())
        {
            if (_bivouacStuckSince <= 0f)
                _bivouacStuckSince = Time.unscaledTime;

            if (Time.unscaledTime - _bivouacStuckSince >= BivouacStuckResumeSeconds)
            {
                LoggerInstance.Warning(
                    "[State] Bivouac flag appears stuck (game=InGame, pawn moved) — forcing gameplay sync resume");
                _bivouacStuckSince = 0f;
                return false;
            }

            return true;
        }

        _bivouacStuckSince = 0f;
        return true;
    }

    /// <summary>
    /// Heuristique : le flag bivouac est probablement coince si le jeu lui-meme
    /// rapporte InGame, qu'on est sur une scene de gameplay, et que le pawn s'est
    /// deplace de facon significative depuis l'entree en bivouac. Conservateur par
    /// construction : si l'un des signaux manque, on suppose un vrai bivouac.
    /// </summary>
    private bool IsBivouacFlagLikelyStuck()
    {
        if (!IsGameplayRootScene(_currentScene))
            return false;

        if (!CairnGameApi.TryGetRawGameState(out var raw) || raw != CairnGameLifecycleState.InGame)
            return false;

        if (!_hasBivouacSuspendPawnPos)
            return false;

        if (!CairnGameApi.TryGetLocalPlayerPose(out var pos, out _))
            return false;

        return (pos - _bivouacSuspendPawnPos).sqrMagnitude >= BivouacStuckMoveThresholdSqr;
    }

    private string ResolveCurrentSceneAfterUnload(string unloadedScene)
    {
        foreach (var scene in _loadedScenes)
        {
            if (IsGameplayRootScene(scene))
                return scene;
        }

        if (!string.Equals(_lastGameplayScene, unloadedScene, StringComparison.Ordinal))
            return _lastGameplayScene;

        return null;
    }

    private static bool IsGameplayRootScene(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName) || !char.IsDigit(sceneName[0]))
            return false;

        return !sceneName.EndsWith("_Holds", StringComparison.Ordinal)
            && !sceneName.Contains("_Holds", StringComparison.Ordinal)
            && !sceneName.EndsWith("_Gameplay", StringComparison.Ordinal)
            && !sceneName.EndsWith("_Art", StringComparison.Ordinal)
            && !sceneName.EndsWith("_Audio", StringComparison.Ordinal)
            && !sceneName.EndsWith("_Camera", StringComparison.Ordinal)
            && !sceneName.EndsWith("_LOD", StringComparison.Ordinal)
            && !sceneName.EndsWith("_AlwaysLoaded", StringComparison.Ordinal);
    }

    public override void OnUpdate()
    {
        _timeSinceLastSceneLoad += Time.unscaledDeltaTime;

        // Drain la queue d'actions provenant de callbacks async — doit tourner
        // en premier pour que les mises à jour d'UI post-réseau soient visibles
        // dès le frame suivant.
        while (_mainThreadActions.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex)
            {
                LoggerInstance.Error($"[MainQueue] Action failed: {ex}");
                CrashReporter.ReportCaughtExceptionOnce(ex, "Mod.MainQueue");
            }
        }

        // Failsafe PANIQUE (F10) : force le deblocage des inputs + ferme le chat, quel que
        // soit l'etat. Lu sur le device clavier brut (jamais affecte par le blocage) et place
        // AVANT tout return anticipe d'OnUpdate (bivouac, etc.) pour etre toujours joignable.
        var panicKeyboard = Keyboard.current;
        if (panicKeyboard != null && panicKeyboard.f10Key.wasPressedThisFrame)
        {
            _chat?.ForceClose();
            CairnGameApi.ForceClearInputBlock();
            LoggerInstance.Msg("[CairnMP] Panic: input force-cleared + chat closed (F10)");
        }

        // Maintient le gel des inputs pendant la saisie du chat + ferme si on quitte le jeu.
        _chat?.Update();

        // Pendant la saisie du chat, on bloque AUSSI les raccourcis du mod (E corde, F7/F8,
        // connect, ping...) — sinon taper du texte declenche des actions. Le jeu, lui, est
        // bloque cote InputManager via ReconcileGameplayInput. Ensemble = blocage total.
        bool chatTyping = _chat?.IsTyping == true;

        // Mise à jour de l'injection du bouton dans le menu principal
        if (_currentScene == "MainMenu")
        {
            MainMenuMultiplayerButton.OnUpdate();
            // Debloque FreeRoam : force le champ du tweakable des qu'il est charge (no-op
            // une fois reussi). Complete le postfix Harmony sur la propriete publique.
            CairnGameApi.TryForceFreeRoamTweakableField();
            // Demasque le mode FreeRoam dans la liste des difficultes (isHidden=false).
            CairnGameApi.TryUnhideFreeRoamDifficulty();
        }

        // Pompe la file de callbacks Steam managés (gère aussi l'init différée).
        _lobby?.Pump(Time.unscaledDeltaTime);

        // Verrouille la couche gameplay avant de traiter les paquets reseau.
        UpdateGameplaySyncSuspension();
        // Reprise differee du patch SetFrame (fenetre de grace post-bivouac) — doit
        // tourner chaque frame, y compris pendant la suspension (s'auto-annule alors).
        UpdateDeferredSetFramePatchResume();

        // Traitement des événements réseau
        _network.Update();

        // Expiration des marqueurs de ping (tourne toujours, meme en bivouac).
        PingMarkerManager.Update();

        // Placement de ping en camera libre — tourne avant la suspension gameplay
        // car la freecam/photo mode peut etre traitee comme non-gameplay.
        if (!chatTyping) TickPingInput();

        // Toggle des noms (N) — place AVANT le return de suspension bivouac/photo
        // pour rester joignable en mode photo (ou le gameplay est suspendu).
        if (!chatTyping) TickNameToggleInput();

        // Injection de la ligne "N" dans la legende native du mode photo. Le clone est
        // instancie sous un parent inactif puis depouille de ses composants non-visuels
        // (sinon les handlers d'input dupliques bloquent le jeu) ; injecte seulement
        // quand le mode photo est reellement ouvert.
        try { PhotoModeNamesRow.Tick(); }
        catch (Exception ex) { LoggerInstance.Error($"[PhotoNames] tick failed: {ex.Message}"); }

        // Synchro de l'heure + sommeil — doit tourner AVANT la suspension bivouac
        // (c'est justement au bivouac qu'on dort et qu'on accelere le temps).
        try
        {
            TickTimeSync();
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"[TimeSync] tick failed: {ex.Message}");
        }

        // Pendant le bivouac, Cairn pilote lui-meme le pawn, la camera et les
        // mains du taping. Le mod garde uniquement une presence reseau minimale.
        if (_gameplaySyncSuspended)
        {
            TickBivouacDebug();
            TickSuspendedNetworkPresence();
            TickConnectingStatus();
            return;
        }

        // Gère le clignotement du curseur + le rafraîchissement du lobby pour le panneau de connexion Canvas.
        _panel.Tick(Time.unscaledDeltaTime);

        // Recalcule l'état de cycle de vie local à partir de la scène + handshake + MC.
        var newState = ComputeLocalState();
        SetLocalState(newState);

        // Diagnostic : surveille le retablissement de la synchro apres un bivouac.
        TickBivouacRecoveryDebug();

        // Flux de lancement de partie
        TickStartGameFlow();

        // Diffusion périodique de l'état du joueur local + synchronisation des fantômes distants.
        try
        {
            TickPlayerSync();
        }
        catch (Exception ex)
        {
            LoggerInstance.Error(ex.ToString());
            CrashReporter.ReportCaughtExceptionOnce(ex, "Mod.TickPlayerSync");
        }

        // Statut d'attente progressif pendant la création/join de lobby (provisioning).
        TickConnectingStatus();

        // Maintien post-teleport longue distance (empeche la chute dans le vide + corrige le
        // repositionnement du chargement de zone). No-op si aucun teleport en attente.
        CairnGameApi.TickTeleportSettle(LocalState == PlayerState.InGame);

        // Encordement entre joueurs : detection clip (E) + entretien de la cordee NATIVE.
        // Plus de corde cosmetique : la corde native de la lifeline (clippee sur un piton
        // mobile pose chez le partenaire, systeme Episure) est le visuel ET l'assurage.
        try
        {
            if (!chatTyping) TickRopeCouple();   // le E ne doit pas clipper pendant la frappe
        }
        catch (Exception ex) { LoggerInstance.Error($"[RopeCouple] tick failed: {ex.Message}"); }

        // Raccourcis clavier via le nouveau Input System
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        // Chat ouvert -> aucun raccourci mod ne passe (blocage total).
        if (chatTyping) return;

        if (keyboard[_connectKey].wasPressedThisFrame)
        {
            if (_panel.IsVisible)
            {
                _panel.Hide();
            }
            else
            {
                if (_currentScene != "MainMenu")
                {
                    LoggerInstance.Msg("[CairnMP] Multiplayer panel is only available from the main menu.");
                    return;
                }

                MainMenuMultiplayerButton.HideModeSelect();
                _panel.Show();
            }
        }

        if (keyboard[_disconnectKey].wasPressedThisFrame)
        {
            if (_network.IsConnected)
            {
                OnDisconnectRequested();
            }
        }

        // Tant que le panneau multijoueur est ouvert, re-affirme le blocage des action maps du menu
        // pour empecher toute navigation en arriere-plan (Suppr, fleches, retour).
        if (_panel != null && _panel.IsVisible)
            CairnGameApi.BlockMainMenuActionMaps();
    }

    private static Key ParseKey(string name, Key fallback)
    {
        if (Enum.TryParse<Key>(name, true, out var result))
            return result;
        return fallback;
    }

    private float _pingCooldownUntil;

    /// <summary>
    /// En camera libre, un clic gauche (ou R1 PS5 / RB Xbox = rightShoulder) pose
    /// un ping a l'endroit vise. Affichage local immediat + envoi reseau.
    /// </summary>
    private void TickPingInput()
    {
        if (_network == null)
            return;

        if (!CairnGameApi.TryIsFreecamActive(out var freecamActive) || !freecamActive)
            return;

        if (Time.unscaledTime < _pingCooldownUntil)
            return;

        var pressed = Mouse.current?.leftButton.wasPressedThisFrame == true
                      || Gamepad.current?.rightShoulder.wasPressedThisFrame == true;
        if (!pressed)
            return;

        // On vise toujours dans la direction de la camera (centre ecran) — simple
        // et coherent clavier/souris comme manette.
        if (!CairnGameApi.TryComputePingPoint(out var point))
            return;

        _pingCooldownUntil = Time.unscaledTime + Protocol.PingCooldownSeconds;

        // Affichage local immediat (visible meme en solo). L'emetteur ignore l'echo
        // serveur de son propre id. L'envoi reseau ne se fait que si on est connecte.
        PingMarkerManager.Spawn(_network.LocalPlayerId, point);
        if (_network.IsHandshakeComplete)
            _network.SendPingPlaced(point);
        LoggerInstance.Msg($"[Ping] Placed @ ({point.x:F1},{point.y:F1},{point.z:F1})");
    }

    /// <summary>
    /// Touche N : bascule l'affichage des plaques de nom des joueurs distants.
    /// Fonctionne en jeu comme en mode photo (l'indication apparait alors en bas
    /// a gauche via PhotoModeHud). Lu sur le device clavier brut.
    /// </summary>
    private void TickNameToggleInput()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb[Key.N].wasPressedThisFrame)
        {
            var shown = RemotePlayerManager.ToggleNames();
            LoggerInstance.Msg($"[CairnMP] Player names {(shown ? "shown" : "hidden")} (N)");
        }
    }

    private void ResetGameplaySyncTimers()
    {
        ResetPlayerSyncDebugState();
        _stateTickTimer = 0f;
        _boneTickTimer = 0f;
        _weatherTickTimer = Protocol.WeatherStateUpdateIntervalSeconds;
    }

    private void TickBivouacDebug()
    {
        if (Time.unscaledTime < _nextBivouacDebugLogAt)
            return;

        _nextBivouacDebugLogAt = Time.unscaledTime + BivouacDebugLogIntervalSeconds;
        LogBivouacDebug("heartbeat");
    }

    private void LogBivouacDebug(string phase)
    {
        var elapsed = _bivouacSuspendedSince > 0f
            ? Math.Max(0f, Time.unscaledTime - _bivouacSuspendedSince)
            : 0f;
        var networkState = _network == null
            ? "network=null"
            : $"network=connected:{_network.IsConnected} handshake:{_network.IsHandshakeComplete} remotes:{_network.RemotePlayers.Count}";

        LoggerInstance.Msg(
            $"[BivouacDebug] phase={phase} elapsed={elapsed:0.0}s scene='{_currentScene ?? ""}' " +
            $"lastGameplay='{_lastGameplayScene ?? ""}' local={LocalState} suspended={_gameplaySyncSuspended} " +
            $"{DescribeRemoteStates()} " +
            $"panelVisible={_panel?.IsVisible == true} {networkState} {RemotePlayerManager.DebugSummary()} " +
            $"setFramePatchInstalled={CairnGameApi.IsNetplaySetFramePatchInstalled} " +
            $"patchPaused={_netplaySetFramePatchPausedForBivouac} " +
            CairnGameApi.BuildBivouacDebugSnapshot());
    }

    /// <summary>
    /// Resume l'etat de cycle de vie de chaque joueur distant connu. Sert a
    /// diagnostiquer le desync de bivouac : si un cote reste a Loading apres la
    /// sortie, les fantomes ne reapparaissent jamais.
    /// </summary>
    private string DescribeRemoteStates()
    {
        if (_network == null)
            return "remoteStates=none";

        var summary = "remoteStates=[";
        var first = true;
        foreach (var kv in _network.RemotePlayers)
        {
            if (!first)
                summary += ",";
            first = false;
            var rp = kv.Value;
            summary += $"{kv.Key}:{(rp == null ? "null" : rp.State.ToString())}";
        }
        return summary + "]";
    }

    /// <summary>
    /// Apres la sortie d'un bivouac, logge periodiquement l'etat local + distant +
    /// le nombre de fantomes pour verifier que la synchro se retablit. Capture le
    /// cas ou les deux cotes sortent mais aucun fantome ne respawn (deadlock).
    /// </summary>
    private void TickBivouacRecoveryDebug()
    {
        if (_bivouacRecoveryWatchUntil <= 0f)
            return;

        var now = Time.unscaledTime;
        if (now >= _bivouacRecoveryWatchUntil)
        {
            _bivouacRecoveryWatchUntil = 0f;
            LogBivouacDebug("recovery-end");
            return;
        }

        if (now < _nextBivouacRecoveryLogAt)
            return;

        _nextBivouacRecoveryLogAt = now + BivouacRecoveryLogIntervalSeconds;
        LogBivouacDebug("recovery");
    }

    // Pause/reprise du patch SetFrame au bivouac via un simple flag (le patch reste
    // installe). Avant on faisait Uninstall/Install (UnpatchSelf/Patch) ici, mais ce
    // churn Harmony tombait dans la fenetre de sauvegarde native du bivouac et pouvait
    // casser le scellage/reouverture du package de save -> 1 save OK puis plus rien.
    private void PauseNetplaySetFramePatchForBivouac()
    {
        // Annule une reprise differee en cours (on re-rentre en bivouac avant la fin
        // de la fenetre de grace) -> le patch doit rester en pause.
        _setFramePatchResumeAt = 0f;

        if (_netplaySetFramePatchPausedForBivouac)
            return;

        _netplaySetFramePatchPausedForBivouac = true;
        CairnGameApi.PauseSetFramePatch();
        LoggerInstance.Msg("[State] Netplay SetFrame patch paused for bivouac");
    }

    /// <summary>
    /// Reprend le patch SetFrame. <paramref name="immediate"/> = true pour les
    /// teardown (deconnexion, leave) ou aucune sauvegarde n'est en cours ; false a la
    /// sortie de bivouac, ou l'on differe la reprise (cf. <see cref="SetFramePatchResumeGraceSeconds"/>)
    /// pour laisser le package de save natif se sceller avant de reactiver l'injection.
    /// </summary>
    private void ResumeNetplaySetFramePatchAfterBivouac(bool immediate = true)
    {
        if (immediate)
        {
            _setFramePatchResumeAt = 0f;
            if (!_netplaySetFramePatchPausedForBivouac)
                return;

            _netplaySetFramePatchPausedForBivouac = false;
            CairnGameApi.ResumeSetFramePatch();
            LoggerInstance.Msg("[State] Netplay SetFrame patch resumed after bivouac");
            return;
        }

        if (!_netplaySetFramePatchPausedForBivouac)
            return;

        _setFramePatchResumeAt = Time.unscaledTime + SetFramePatchResumeGraceSeconds;
        LoggerInstance.Msg(
            $"[State] Netplay SetFrame patch resume scheduled in {SetFramePatchResumeGraceSeconds:F0}s (save-seal grace)");
    }

    /// <summary>Applique la reprise differee du patch SetFrame programmee a la sortie de bivouac.</summary>
    private void UpdateDeferredSetFramePatchResume()
    {
        if (_setFramePatchResumeAt <= 0f)
            return;

        // Toujours en bivouac / hors gameplay -> on annule la reprise (on restera en
        // pause tant qu'on n'est pas revenu en jeu de facon stable).
        if (_gameplaySyncSuspended || ShouldSuspendGameplaySync())
        {
            _setFramePatchResumeAt = 0f;
            return;
        }

        if (Time.unscaledTime < _setFramePatchResumeAt)
            return;

        _setFramePatchResumeAt = 0f;
        if (!_netplaySetFramePatchPausedForBivouac)
            return;

        _netplaySetFramePatchPausedForBivouac = false;
        CairnGameApi.ResumeSetFramePatch();
        LoggerInstance.Msg("[State] Netplay SetFrame patch resumed after bivouac (save-seal grace elapsed)");
    }

    private void SetLocalState(PlayerState state)
    {
        if (state == LocalState)
            return;

        LoggerInstance.Msg($"[State] local: {LocalState} -> {state}");
        LocalState = state;
    }

    public override void OnGUI()
    {
        _panel.OnGUI();
        PingMarkerManager.OnGUI();
        _chat?.OnGUI();
    }

    private void OnStartRequested()
    {
        if (_pendingStart.HasValue)
        {
            _panel.SetStatus("Launch already in progress.", true);
            return;
        }
        if (_lobby == null || !_lobby.IsInLobby)
        {
            _panel.SetStatus("Not connected to a lobby.", false);
            return;
        }
        if (!_lobby.IsHost)
        {
            _panel.SetStatus("Only the host can start.", true);
            return;
        }

        _panel.SetStatus("Starting lobby...", true);
        _lobby.BroadcastStart(BuildDefaultStartGame());
    }

    private static ServerStartGame BuildDefaultStartGame() => new()
    {
        Difficulty = (int)GameDifficulty.Explorer,
        SkipTutorials = true,
        SkipPractice = true,
        AssistEnabled = false,
    };

    private void BeginStartGame(ServerStartGame pkt)
    {
        MainMenuMultiplayerButton.RestoreMainMenuInput();
        _pendingStart = pkt;
        _pendingStartRetries = 0;
        _pendingStartRetryTimer = 1.0f;
        _panel.SetStatus($"Launching game ({(GameDifficulty)pkt.Difficulty})...", true);
    }

    private async void OnHostRequested(HostConfig cfg)
    {
        var playerName = GetSteamPlayerName();
        cfg = new HostConfig
        {
            PlayerName = playerName,
            LobbyName = cfg.LobbyName,
            MaxPlayers = cfg.MaxPlayers,
            Visibility = cfg.Visibility,
        };

        LoggerInstance.Msg($"Creating Steam lobby '{cfg.LobbyName}' as '{cfg.PlayerName}' " +
                           $"(max {cfg.MaxPlayers}, {cfg.Visibility})...");
        ModConfig.PlayerName.Value = cfg.PlayerName;
        ModConfig.MaxPlayers.Value = cfg.MaxPlayers;
        MelonPreferences.Save();

        _panel.SetCurrentLobbyName(cfg.LobbyName);
        BeginConnecting("Creating lobby");

        try
        {
            var ok = await _lobby.CreateLobby(cfg);
            if (!ok)
            {
                _connectingStart = null;
                if (string.IsNullOrEmpty(_lastLobbyError))
                    _panel.SetStatus("Failed to create lobby.", false);
            }
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"[CairnMP] CreateLobby failed: {ex}");
            _connectingStart = null;
            _panel.SetStatus($"Failed: {ex.Message}", false);
        }
    }

    /// <summary>Browser : appelle SteamMatchmaking.RequestLobbyList et pousse le résultat à l'UI.</summary>
    private async void OnBrowseRequested()
    {
        LoggerInstance.Msg("[Browse] Requesting Steam lobby list...");
        try
        {
            var lobbies = await _lobby.RequestLobbyList();
            _panel.SetBrowserLobbies(lobbies);
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"[Browse] Failed: {ex}");
            _panel.SetBrowserLobbies(System.Array.Empty<LobbyEntry>());
        }
    }

    /// <summary>Join via SteamID64 (browser ou Steam invite).</summary>
    private async void OnJoinByLobbyIdRequested(ulong lobbyId)
    {
        LoggerInstance.Msg($"[Browse] Joining lobby {lobbyId}...");
        BeginConnecting("Joining lobby");
        try
        {
            var ok = await _lobby.JoinById(lobbyId);
            if (!ok)
            {
                _connectingStart = null;
                if (string.IsNullOrEmpty(_lastLobbyError))
                    _panel.SetStatus("Failed to join lobby.", false);
            }
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"[CairnMP] JoinById failed: {ex}");
            _connectingStart = null;
            _panel.SetStatus($"Failed: {ex.Message}", false);
        }
    }

    private async void OnJoinByCodeRequested(string playerName, string roomCode)
    {
        playerName = GetSteamPlayerName();
        LoggerInstance.Msg($"Joining lobby '{roomCode}' as '{playerName}'...");
        ModConfig.PlayerName.Value = playerName;

        BeginConnecting($"Joining {roomCode}");

        try
        {
            var ok = await _lobby.JoinByCode(roomCode);
            if (!ok)
            {
                _connectingStart = null;
                if (string.IsNullOrEmpty(_lastLobbyError))
                    _panel.SetStatus($"Lobby {roomCode} not found.", false);
            }
        }
        catch (Exception ex)
        {
            LoggerInstance.Error($"[CairnMP] JoinByCode failed: {ex}");
            _connectingStart = null;
            _panel.SetStatus($"Failed: {ex.Message}", false);
        }
    }

    /// <summary>Démarre le tracking de progression et pose le premier statut.</summary>
    private void BeginConnecting(string verb)
    {
        _connectingVerb  = verb;
        _connectingStart = DateTime.UtcNow;
        _lastLobbyError  = null;
        _panel.SetConnecting($"{verb}...");
    }

    private void OnDisconnectRequested()
    {
        _lobby?.Leave();
        _network.Disconnect();
        CairnGameApi.ResetRemoteWeatherSyncState();
        RemotePlayerManager.ClearAll();
        PingMarkerManager.ClearAll();
        _gameplaySyncSuspended = false;
        ResumeNetplaySetFramePatchAfterBivouac();
        _panel.SetStatus("Disconnected", false);
        LoggerInstance.Msg("Left lobby.");
    }

    private string GetSteamPlayerName()
    {
        var name = _lobby?.LocalPersonaName;
        if (!string.IsNullOrWhiteSpace(name))
            return name.Trim();

        return string.IsNullOrWhiteSpace(ModConfig.PlayerName?.Value)
            ? "Player"
            : ModConfig.PlayerName.Value.Trim();
    }

    /// <summary>Adapte le statut affiché pendant qu'on attend l'aller-retour Steam
    /// pour la création / le join du lobby. Pas de provisioning serveur ici — le
    /// callback est typiquement &lt; 1 s — donc messages courts seulement.</summary>
    private void TickConnectingStatus()
    {
        if (!_connectingStart.HasValue) return;
        // L'event OnLobbyEntered effacera _connectingStart et écrira "Connected".
        if (_lobby != null && _lobby.IsInLobby) { _connectingStart = null; return; }

        var elapsed = (DateTime.UtcNow - _connectingStart.Value).TotalSeconds;
        string msg = elapsed switch
        {
            < 3  => $"{_connectingVerb}...",
            < 10 => $"{_connectingVerb} — waiting for Steam...",
            _    => $"{_connectingVerb} — taking longer than usual...",
        };
        _panel.SetConnecting(msg);
    }

    public override void OnDeinitializeMelon()
    {
        CairnGameApi.UninstallNetplaySetFramePatch();
        _lobby?.Dispose();
        _network?.Dispose();
        LoggerInstance.Msg("Cairn Multiplayer Mod unloaded.");
    }
}
