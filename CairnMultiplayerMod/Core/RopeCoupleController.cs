using System.Collections.Generic;
using CairnMultiplayer.Shared;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Core;

public partial class Mod
{
    private const float RopeClipRangeMeters = 3f;

    // Anti-spam : on ne diffuse l'unclip "hard" (mort, game over, menu, deco) qu'une seule
    // fois par episode. Re-arme des qu'on quitte l'etat dangereux.
    private bool _ropeHardTornDown;

    /// <summary>
    /// Encordement coop, appele chaque frame : l'input E (re)bascule l'intention de lien
    /// (ClientRopeClip, relaye par l'hote), puis on entretient la cordee NATIVE (corde de la
    /// lifeline locale clippee sur un piton mobile pose chez le partenaire, systeme repris
    /// d'Episure, cf. CairnGameApi.UpdateRopeTeamAnchor).
    /// </summary>
    private void TickRopeCouple()
    {
        if (_network == null)
            return;

        // Securite encordement : tourne TOUJOURS (meme hors InGame) pour garantir le
        // demantelement des liens/ancres dans un maximum de situations (mort, game over,
        // menu, deconnexion, chargement, bivouac, partenaire parti).
        TickRopeSafety();

        if (LocalState != PlayerState.InGame)
            return;

        HandleRopeClipInput();
        TickRopeTeam();
    }

    /// <summary>
    /// Filet de securite du systeme d'encordement, evalue chaque frame. Classe l'etat local :
    ///  - HARD (mort, game over, retour menu principal, deconnexion) : on ROMPT le lien logique
    ///    et on previent le(s) partenaire(s) (unclip fiable diffuse), puis on relache les ancres
    ///    natives. Diffuse une seule fois par episode (flag _ropeHardTornDown).
    ///  - SOFT (chargement / streaming de scene / bivouac, bref tout etat non-InGame transitoire) :
    ///    on GARDE le lien logique (il doit survivre aux transitions) mais on relache les ancres
    ///    natives — elles seront recreees au retour InGame par TickRopeTeam.
    /// Le partenaire qui meurt/part diffuse lui-meme son unclip (ou OnPlayerLeft le retire), donc
    /// chaque cote nettoie son propre etat : pas besoin de detecter la mort distante ici.
    /// NB : la pause MP n'est PAS un cas hard (la scene reste une scene de gameplay, pas MainMenu).
    /// </summary>
    private void TickRopeSafety()
    {
        bool connected = _network.IsConnected && _network.IsHandshakeComplete;
        bool inGame = LocalState == PlayerState.InGame;
        bool dead = CairnGameApi.GetLocalPawnState() == NetFrame.PawnStateType.Dead;
        bool gameOver = CairnGameApi.TryGetGameLifecycle(out var lifecycle, out _)
                        && lifecycle == CairnGameLifecycleState.GameOver;
        bool atMainMenu = _currentScene != null && _currentScene.StartsWith("MainMenu");

        bool hardUnsafe = !connected || dead || gameOver || atMainMenu;

        if (hardUnsafe)
        {
            if (CairnGameApi.HasRopeTeamAnchors)
                CairnGameApi.ReleaseAllRopeTeamAnchors();

            if (!_ropeHardTornDown)
            {
                _ropeHardTornDown = true;
                string reason = !connected ? "disconnected"
                    : dead ? "local player died"
                    : gameOver ? "game over"
                    : "returned to main menu";
                TearDownAllLocalRopeLinks(reason);
            }
            return;
        }

        // Sorti de l'etat dangereux -> re-arme la diffusion pour le prochain episode.
        _ropeHardTornDown = false;

        // Etat transitoire (chargement / bivouac) : garde le lien, relache juste les ancres
        // natives pour ne pas laisser une corde pinnee sur un objet en cours de destruction.
        if (!inGame && CairnGameApi.HasRopeTeamAnchors)
            CairnGameApi.ReleaseAllRopeTeamAnchors();
    }

    /// <summary>
    /// Rompt tous les liens d'encordement impliquant le joueur local : diffuse un unclip fiable
    /// a chaque partenaire (si le reseau repond encore) puis retire le lien localement tout de
    /// suite (cote client, on n'attend pas l'echo de l'hote). Idempotent.
    /// </summary>
    private void TearDownAllLocalRopeLinks(string reason)
    {
        int self = _network.LocalPlayerId;

        var partners = new List<int>();
        foreach (var (a, b) in RopeLinkState.Links())
        {
            if (a == self) partners.Add(b);
            else if (b == self) partners.Add(a);
        }
        if (partners.Count == 0)
            return;

        bool canNotify = _network.IsConnected && _network.IsHandshakeComplete;
        foreach (var partner in partners)
        {
            if (canNotify)
                _network.SendRopeClip(partner, false);
            RopeLinkState.Apply(self, partner, false);
        }

        LoggerInstance.Msg($"[RopeCouple] Auto-unclipped {partners.Count} link(s): {reason}");
    }

    /// <summary>
    /// Input E : (re)bascule un lien de corde avec le fantome le plus proche. On envoie
    /// seulement l'intention ; RopeLinkState gere l'etat autoritaire du lien. La cordee
    /// native (corde + assurage) est entretenue par TickRopeTeam, pas ici.
    /// </summary>
    private void HandleRopeClipInput()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null || !keyboard[Key.E].wasPressedThisFrame)
            return;

        if (!CairnGameApi.TryGetLocalPlayerPose(out var localPos, out _))
            return;

        // Fantome InGame le plus proche dans le rayon. NB : on garde un flag dedie plutot
        // qu'un sentinel negatif sur bestId — le debug mirror a un id negatif (-777) qui
        // entrerait en collision avec un sentinel "-1 = aucun".
        bool found = false;
        int bestId = 0;
        float bestDist = RopeClipRangeMeters;
        foreach (var kv in _network.RemotePlayers)
        {
            var rp = kv.Value;
            if (rp == null || rp.State != PlayerState.InGame) continue;
            var d = Vector3.Distance(localPos, new Vector3(rp.X, rp.Y, rp.Z));
            if (d < bestDist)
            {
                bestDist = d;
                bestId = kv.Key;
                found = true;
            }
        }

        if (!found)
        {
            // Diagnostic : pourquoi rien ne se passe ? Dump id + State + distance de chaque remote.
            int total = _network.RemotePlayers.Count;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in _network.RemotePlayers)
            {
                var rp = kv.Value;
                if (rp == null) { sb.Append($" [{kv.Key}:null]"); continue; }
                float d = Vector3.Distance(localPos, new Vector3(rp.X, rp.Y, rp.Z));
                sb.Append($" [{kv.Key} state={rp.State} dist={d:F1}m hasFrame={rp.HasPlayerFrame}]");
            }
            LoggerInstance.Msg($"[RopeCouple] E pressed — no eligible ghost within {RopeClipRangeMeters:F0}m " +
                $"(remote players: {total}):{(total == 0 ? " none" : sb.ToString())}");
            return;
        }

        int self = _network.LocalPlayerId;

        // Deja encorde avec ce fantome -> decordage.
        if (RopeLinkState.IsLinked(self, bestId))
        {
            _network.SendRopeClip(bestId, false);
            LoggerInstance.Msg($"[RopeCouple] Unclip from player {bestId}");
            return;
        }

        // Encordage (v1 : un seul lien par joueur -> on lache l'eventuel partenaire actuel).
        int current = RopeLinkState.PartnerOf(self);
        if (current >= 0 && current != bestId)
            _network.SendRopeClip(current, false);
        _network.SendRopeClip(bestId, true);
        LoggerInstance.Msg($"[RopeCouple] Clip to player {bestId} at {bestDist:F1}m");
    }

    /// <summary>
    /// Cordee NATIVE (systeme Episure), chaque frame. Tant qu'un partenaire est encorde, on
    /// entretient une ancre (piton clone, sans son de pose) posee sur le baudrier du partenaire
    /// et on y clippe la corde de la lifeline locale (UpdateRopeTeamAnchor). La corde native
    /// sert alors a la fois de VISUEL (les deux clients voient leur propre corde, c'est
    /// symetrique) et d'ASSURAGE (une chute est retenue nativement : suspension, pas de mort ni
    /// de drain). Sans partenaire, on relache. Tout vient des positions deja synchronisees ->
    /// rien de neuf a transmettre.
    /// </summary>
    private void TickRopeTeam()
    {
        int self = _network.LocalPlayerId;
        int partner = RopeLinkState.PartnerOf(self);

        if (partner < 0 || !RemotePlayerManager.TryGetGhostHarnessAttachPosition(partner, out var partnerAnchor))
        {
            if (CairnGameApi.HasRopeTeamAnchors) CairnGameApi.ReleaseAllRopeTeamAnchors();
            return;
        }

        CairnGameApi.UpdateRopeTeamAnchor(partner, partnerAnchor);
    }

    /// <summary>Vide tous les liens d'encordement + leurs cordes (deconnexion / retour menu).</summary>
    private void ClearRopeLinks()
    {
        RopeLinkState.Clear();
        CairnGameApi.ReleaseAllRopeTeamAnchors();
        _ropeHardTornDown = false;
    }

    /// <summary>
    /// Reset local sur changement de scene (appele par PlayerStateBroadcaster). No-op pour
    /// l'encordement : le lien est global (RopeLinkState) et persiste a travers le streaming
    /// de scene ; le nettoyage se fait a la deconnexion via ClearRopeLinks.
    /// </summary>
    private void ResetRopeCoupleState() { }
}
