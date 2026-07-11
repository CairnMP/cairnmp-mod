using System;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Networking;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnMultiplayerMod.Core.Chat;

/// <summary>
/// Parse et execute les commandes de chat ("/nom args"). Les commandes de
/// teleportation sont reservees a l'hote : le controle de role se fait ICI, sur la
/// machine qui tape la commande. Un non-hote voit un refus local et AUCUN paquet
/// n'est emis (on ne fait jamais confiance au client cote reseau).
/// </summary>
internal sealed class CommandRouter
{
    private readonly NetworkManager _network;
    private readonly Func<bool> _isHost;
    private readonly Action<string> _systemLine;

    public CommandRouter(NetworkManager network, Func<bool> isHost, Action<string> systemLine)
    {
        _network = network;
        _isHost = isHost;
        _systemLine = systemLine;
    }

    /// <summary>
    /// Traite une ligne de chat. Retourne true si c'etait une commande (consommee),
    /// false si c'est un message normal a diffuser.
    /// </summary>
    public bool TryHandle(string input)
    {
        var cmd = CommandParser.Parse(input);
        if (!cmd.IsCommand) return false;

        switch (cmd.Name)
        {
            case "help": HandleHelp(); break;
            case "tp": HandleTp(cmd.ArgsText); break;
            case "bring": HandleBring(cmd.ArgsText); break;
            case "": _systemLine("Type /help for the list of commands."); break;
            default: _systemLine($"Unknown command: /{cmd.Name}. Type /help."); break;
        }
        return true;
    }

    private void HandleHelp()
    {
        _systemLine("Commands:");
        _systemLine("  /help - show this help");
        if (_isHost())
        {
            _systemLine("  /tp <player> - teleport yourself to a player (they must be walking)");
            _systemLine("  /bring <player> - teleport a player to you (you must be walking)");
        }
    }

    private void HandleTp(string targetName)
    {
        if (!RequireHost()) return;
        if (string.IsNullOrWhiteSpace(targetName)) { _systemLine("Usage: /tp <player>"); return; }
        if (CairnGameApi.IsLocalInBivouac()) { _systemLine("Can't teleport while you are in a bivouac."); return; }
        if (!TryResolvePlayer(targetName, out var p)) return;
        if (!IsTeleportTargetReady(p)) return;
        // On ne se teleporte que vers un joueur qui MARCHE : atterrir sur un grimpeur (paroi)
        // ferait spawner sur le mur -> chute / bug.
        if (!IsRemoteWalking(p))
        {
            _systemLine($"Can't teleport to {p.Name}: they must be walking (not climbing or falling).");
            return;
        }

        // L'hote connait deja la position du joueur : teleportation 100% locale.
        if (CairnGameApi.TeleportLocalPlayer(new Vector3(p.X, p.Y, p.Z), p.YawDeg))
            _systemLine($"Teleported to {p.Name}.");
        else
            _systemLine("Can't teleport right now (not in game?).");
    }

    private void HandleBring(string targetName)
    {
        if (!RequireHost()) return;
        if (string.IsNullOrWhiteSpace(targetName)) { _systemLine("Usage: /bring <player>"); return; }
        if (CairnGameApi.IsLocalInBivouac()) { _systemLine("Can't bring while you are in a bivouac."); return; }
        // On ne ramene quelqu'un que si NOUS marchons : la cible atterrit a notre position, donc
        // celle-ci doit etre un sol sur (pas en pleine paroi) sous peine de chute / bug.
        if (!CairnGameApi.IsLocalPlayerWalking())
        {
            _systemLine("You must be walking to bring someone (not climbing or falling).");
            return;
        }
        if (!TryResolvePlayer(targetName, out var p)) return;
        if (!IsTeleportTargetReady(p)) return;

        // L'hote ne peut pas bouger le perso d'un autre : il demande au client cible
        // de se teleporter vers la position de l'hote via un ServerTeleport dedie.
        if (!CairnGameApi.TryGetLocalPlayerPose(out var pos, out var yaw))
        {
            _systemLine("Can't bring right now (not in game?).");
            return;
        }
        if (_network.SendTeleportToPlayer(p.Id, pos.x, pos.y, pos.z, yaw))
            _systemLine($"Bringing {p.Name} to you.");
        else
            _systemLine($"Could not reach {p.Name}.");
    }

    /// <summary>
    /// Vrai si la cible est teleportable. Un joueur en bivouac (ou en chargement/menu)
    /// diffuse un etat != InGame et une position figee/perimee : on refuse alors la
    /// teleportation pour ne pas l'arracher de son bivouac ni viser une position obsolete.
    /// </summary>
    private bool IsTeleportTargetReady(RemotePlayer p)
    {
        if (p.State == PlayerState.InGame) return true;
        _systemLine($"{p.Name} is not available right now (in a bivouac, loading, or in a menu).");
        return false;
    }

    /// <summary>
    /// Vrai si le joueur distant MARCHE (PawnState Walking), decode depuis sa derniere NetFrame.
    /// False si pas de frame, ou s'il grimpe / chute / est mort -> teleportation refusee.
    /// </summary>
    private bool IsRemoteWalking(RemotePlayer p)
    {
        if (!p.HasPlayerFrame) return false;
        return CairnGameApi.GetPawnStateFromFrame(p.PlayerFrame) == NetFrame.PawnStateType.Walking;
    }

    private bool RequireHost()
    {
        if (_isHost()) return true;
        _systemLine("You are not the host.");
        return false;
    }

    /// <summary>
    /// Resout un joueur par pseudo (insensible a la casse) parmi les joueurs distants.
    /// Match exact prioritaire, sinon prefixe unique. Affiche un message d'erreur et
    /// retourne false si rien (ou plusieurs candidats ambigus).
    /// </summary>
    private bool TryResolvePlayer(string name, out RemotePlayer player)
    {
        player = null;
        RemotePlayer exact = null;
        RemotePlayer prefix = null;
        int prefixCount = 0;

        foreach (var p in _network.RemotePlayers.Values)
        {
            if (p?.Name == null) continue;
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { exact = p; break; }
            if (p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)) { prefix = p; prefixCount++; }
        }

        var match = exact ?? (prefixCount == 1 ? prefix : null);
        if (match == null)
        {
            _systemLine(prefixCount > 1
                ? $"Ambiguous player name: {name}"
                : $"Player not found: {name}");
            return false;
        }
        player = match;
        return true;
    }
}
