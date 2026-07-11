using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;

namespace CairnMultiplayerMod.Networking.Authoritative;

// ============================================================================
// Phase 3 — Host autoritatif : couture agnostique du transport.
//
// Constat (2026-07) : l'hote fait DEJA autorite, mais la logique (validation,
// roster, snapshots, rediffusion "Server*" a tous-sauf-l'emetteur) est noyee inline
// dans SteamP2PTransport — melangee au "comment les octets voyagent". Impossible a
// tester hors-jeu, et chaque nouvelle synchro doit re-cabler la meme plomberie.
//
// Objectif : extraire cette logique dans un SEUL cœur autoritatif
// (IAuthoritativeSession) ou vivent l'autorite, la validation, l'etat officiel et les
// snapshots. Le transport n'est plus qu'un IAuthoritativeSink : il convoie les octets,
// il ne decide rien. On reste tout-Steam ; ce decouplage garde le cœur testable et
// pret pour la migration du transport vers SteamNetworkingSockets.
//
// Ces types ne dependent QUE du protocole partage (PacketId / IPacket) — aucun type
// Il2Cpp, aucun SteamId. C'est ce qui les rend testables hors-jeu.
// ============================================================================

/// <summary>
/// Fiabilite d'envoi, agnostique du transport. Chaque sink la traduit vers son monde :
/// LiteNetLib <see cref="!:DeliveryMethod"/>, ou les flags SteamNetworking.
/// </summary>
public enum NetReliability
{
    /// <summary>Position / bones / netframes : perte toleree, la derniere valeur gagne.</summary>
    UnreliableSequenced,
    /// <summary>Pitons / chat / handshake / snapshots : livraison garantie et ordonnee.</summary>
    ReliableOrdered,
}

/// <summary>
/// Canal de SORTIE du cœur autoritatif vers le transport. Le cœur raisonne uniquement
/// en <c>playerId</c> (int) ; c'est le sink qui sait a quel NetPeer / SteamId cela
/// correspond et comment acheminer les octets.
/// </summary>
public interface IAuthoritativeSink
{
    /// <summary>Les playerId actuellement connectes (pour iterer / snapshots).</summary>
    IReadOnlyCollection<int> ConnectedPlayerIds { get; }

    /// <summary>Envoie un packet a un joueur precis.</summary>
    void SendTo(int playerId, PacketId id, IPacket packet, NetReliability reliability);

    /// <summary>
    /// Diffuse un packet a tous les joueurs, en excluant eventuellement l'emetteur.
    /// <paramref name="exceptPlayerId"/> = 0 signifie "a tout le monde, emetteur inclus"
    /// (utile pour une confirmation autoritaire, cf. ServerRopeClip).
    /// </summary>
    void Broadcast(PacketId id, IPacket packet, int exceptPlayerId, NetReliability reliability);
}

/// <summary>
/// Cœur autoritatif agnostique du transport : detient l'etat officiel de la session
/// (roster, pitons, meteo, lampes, cosmetiques...) et applique les regles. Unique
/// endroit ou vivent l'autorite et la validation. Alimente par un transport via
/// <see cref="OnClientPacket"/>, il repond en diffusant les "Server*" par le sink.
/// </summary>
public interface IAuthoritativeSession
{
    /// <summary>Nouveau joueur admis apres handshake (playerId attribue par le transport).</summary>
    void OnPlayerJoined(int playerId, string playerName);

    /// <summary>Joueur parti (deconnexion / timeout) : purge son etat, notifie les autres.</summary>
    void OnPlayerLeft(int playerId);

    /// <summary>
    /// Un packet <c>Client*</c> recu d'un joueur. Le cœur deserialise, valide, met a jour
    /// l'etat officiel, puis rediffuse le <c>Server*</c> correspondant via le sink.
    /// </summary>
    void OnClientPacket(int playerId, PacketId id, BinaryReader payload);

    /// <summary>
    /// (Re)connexion : pousse l'etat officiel complet (pitons places, meteo courante,
    /// etats de lampe...) au seul joueur cible, pour eliminer les desyncs
    /// "je me suis connecte apres l'evenement".
    /// </summary>
    void SendSnapshotTo(int playerId);
}
