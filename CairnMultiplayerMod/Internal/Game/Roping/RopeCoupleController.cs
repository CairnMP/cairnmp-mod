using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayerMod.Internal.Networking;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Internal.Game.Roping;

internal sealed class RopeCoupleController
{
    private const float RopeClipRangeMeters = 3f;

    private readonly NetworkManager _network;
    private readonly RuntimeState _state;

    // Hard teardown is broadcast once per episode to avoid reliable-packet spam.
    private bool _ropeHardTornDown;

    internal RopeCoupleController(NetworkManager network, RuntimeState state)
    {
        _network = network;
        _state = state ?? throw new System.ArgumentNullException(nameof(state));
    }

    internal void Tick()
    {
        if (_network == null)
            return;

        // Teardown must remain reachable outside InGame.
        if (!TickRopeSafety()) return;

        if (_state.LocalPlayerState != PlayerState.InGame)
            return;

        if (!InputCaptureState.IsKeyboardCaptured)
            HandleRopeClipInput();
        TickRopeTeam();
    }

    /// <summary>
    /// Hard exits break the logical link; transient loading and bivouacs release only native
    /// anchors so the link can survive scene streaming.
    /// </summary>
    private bool TickRopeSafety()
    {
        bool connected = _network.IsConnected && _network.IsHandshakeComplete;
        bool inGame = _state.LocalPlayerState == PlayerState.InGame;
        bool dead = PawnCaptureInterop.GetLocalPawnState() == NetFrame.PawnStateType.Dead;
        bool gameOver = GameLifecycleService.TryGetGameLifecycle(out var lifecycle, out _)
                        && lifecycle == CairnGameLifecycleState.GameOver;
        bool atMainMenu = SceneRoles.IsMainMenuArea(_state.CurrentScene);

        bool hardUnsafe = !connected || dead || gameOver || atMainMenu;

        if (hardUnsafe)
        {
            if (RopeInterop.HasRopeTeamAnchors)
                RopeInterop.ReleaseAllAnchors();

            if (!_ropeHardTornDown)
            {
                _ropeHardTornDown = true;
                string reason = !connected ? "disconnected"
                    : dead ? "local player died"
                    : gameOver ? "game over"
                    : "returned to main menu";
                TearDownAllLocalRopeLinks(reason);
            }
            return false;
        }

        _ropeHardTornDown = false;

        if (!inGame && RopeInterop.HasRopeTeamAnchors)
            RopeInterop.ReleaseAllAnchors();
        return inGame;
    }

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

        ModLog.Info($"[RopeCouple] Auto-unclipped {partners.Count} link(s): {reason}");
    }

    private void HandleRopeClipInput()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null || !keyboard[Key.E].wasPressedThisFrame)
            return;

        // An existing rope must always be releasable, including at zero slack.
        int self = _network.LocalPlayerId;
        int linkedPartner = RopeLinkState.PartnerOf(self);
        if (linkedPartner >= 0)
        {
            _network.SendRopeClip(linkedPartner, false);
            RopeLinkState.Apply(self, linkedPartner, false);
            RopeInterop.ReleaseAllAnchors();
            return;
        }

        if (!LocalPlayerInterop.TryGetPose(out var localPos, out _))
            return;

        // The debug mirror has a negative id, so a separate flag avoids sentinel collisions.
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
            int total = _network.RemotePlayers.Count;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in _network.RemotePlayers)
            {
                var rp = kv.Value;
                if (rp == null) { sb.Append($" [{kv.Key}:null]"); continue; }
                float d = Vector3.Distance(localPos, new Vector3(rp.X, rp.Y, rp.Z));
                sb.Append($" [{kv.Key} state={rp.State} dist={d:F1}m hasFrame={rp.HasPlayerFrame}]");
            }
            ModLog.Info($"[RopeCouple] E pressed — no eligible ghost within {RopeClipRangeMeters:F0}m " +
                $"(remote players: {total}):{(total == 0 ? " none" : sb.ToString())}");
            return;
        }

        if (RopeLinkState.IsLinked(self, bestId))
        {
            _network.SendRopeClip(bestId, false);
            ModLog.Info($"[RopeCouple] Unclip from player {bestId}");
            return;
        }

        int current = RopeLinkState.PartnerOf(self);
        if (current >= 0 && current != bestId)
            _network.SendRopeClip(current, false);
        _network.SendRopeClip(bestId, true);
        ModLog.Info($"[RopeCouple] Clip to player {bestId} at {bestDist:F1}m");
    }

    private void TickRopeTeam()
    {
        int self = _network.LocalPlayerId;
        int partner = RopeLinkState.PartnerOf(self);

        if (partner < 0 || !_network.RemotePlayers.TryGetValue(partner, out var remote)
            || remote == null || remote.State != PlayerState.InGame
            || !RemotePlayerManager.TryGetGhostHarness(partner, out var partnerAnchor))
        {
            if (RopeInterop.HasRopeTeamAnchors) RopeInterop.ReleaseAllAnchors();
            return;
        }

        if (!RopeInterop.UpdateRopeTeamAnchor(partner, partnerAnchor))
        {
            _network.SendRopeClip(partner, false);
            RopeLinkState.Apply(self, partner, false);
            RopeInterop.ReleaseAllAnchors();
            ModLog.Warning("[RopeCouple] Rope attachment was refused; the link has been released.");
        }
    }

    internal void ClearLinks()
    {
        RopeLinkState.Clear();
        RopeInterop.ReleaseAllAnchors();
        _ropeHardTornDown = false;
    }

    /// <summary>Logical links intentionally survive scene streaming and clear only on disconnect.</summary>
    internal void Reset() { }
}
