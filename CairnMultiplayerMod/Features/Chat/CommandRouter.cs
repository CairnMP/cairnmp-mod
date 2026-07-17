using System;
using CairnMultiplayer.Shared;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;

namespace CairnMultiplayerMod.Features.Chat;

/// <summary>
/// Parses and executes chat commands ("/name args"). The teleport commands are
/// host-only: the role check is done HERE, on the machine that types the command. A
/// non-host sees a local refusal and NO packet is sent (we never trust the client on
/// the network side).
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
    /// Processes a chat line. Returns true if it was a command (consumed), false if
    /// it's a normal message to broadcast.
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
        if (GameLifecycleService.IsLocalInBivouac()) { _systemLine("Can't teleport while you are in a bivouac."); return; }
        if (!TryResolvePlayer(targetName, out var p)) return;
        if (!IsTeleportTargetReady(p)) return;
        // We only teleport to a player who is WALKING: landing on a climber (wall)
        // would spawn us on the wall -> fall / bug.
        if (!IsRemoteWalking(p))
        {
            _systemLine($"Can't teleport to {p.Name}: they must be walking (not climbing or falling).");
            return;
        }

        // The host already knows the player's position: 100% local teleport.
        if (TeleportApi.TeleportLocalPlayer(new Vector3(p.X, p.Y, p.Z), p.YawDeg))
            _systemLine($"Teleported to {p.Name}.");
        else
            _systemLine("Can't teleport right now (not in game?).");
    }

    private void HandleBring(string targetName)
    {
        if (!RequireHost()) return;
        if (string.IsNullOrWhiteSpace(targetName)) { _systemLine("Usage: /bring <player>"); return; }
        if (GameLifecycleService.IsLocalInBivouac()) { _systemLine("Can't bring while you are in a bivouac."); return; }
        // We only bring someone if WE are walking: the target lands at our position, so it
        // must be solid ground (not mid-wall) or they'll fall / bug out.
        if (!PawnCaptureApi.IsLocalPlayerWalking())
        {
            _systemLine("You must be walking to bring someone (not climbing or falling).");
            return;
        }
        if (!TryResolvePlayer(targetName, out var p)) return;
        if (!IsTeleportTargetReady(p)) return;

        // The host can't move another player's character: it asks the target client
        // to teleport to the host's position via a dedicated ServerTeleport.
        if (!LocalPlayerApi.TryGetLocalPlayerPose(out var pos, out var yaw))
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
    /// True if the target can be teleported. A player in a bivouac (or loading/menu)
    /// broadcasts a state != InGame and a frozen/stale position: we then refuse the
    /// teleport so as not to yank them out of their bivouac or aim at a stale position.
    /// </summary>
    private bool IsTeleportTargetReady(RemotePlayer p)
    {
        if (p.State == PlayerState.InGame) return true;
        _systemLine($"{p.Name} is not available right now (in a bivouac, loading, or in a menu).");
        return false;
    }

    /// <summary>
    /// True if the remote player is WALKING (PawnState Walking), decoded from their last NetFrame.
    /// False if there's no frame, or if they're climbing / falling / dead -> teleport refused.
    /// </summary>
    private bool IsRemoteWalking(RemotePlayer p)
    {
        if (!p.HasPlayerFrame) return false;
        return PawnCaptureApi.GetPawnStateFromFrame(p.PlayerFrame) == NetFrame.PawnStateType.Walking;
    }

    private bool RequireHost()
    {
        if (_isHost()) return true;
        _systemLine("You are not the host.");
        return false;
    }

    /// <summary>
    /// Resolves a player by nickname (case-insensitive) among the remote players.
    /// Exact match takes priority, otherwise a unique prefix. Displays an error message
    /// and returns false if nothing matches (or several ambiguous candidates).
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
