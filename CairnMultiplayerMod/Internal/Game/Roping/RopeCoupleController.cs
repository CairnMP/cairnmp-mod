using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Life;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayerMod.Internal.Networking;
using CairnMultiplayerMod.Internal.Networking.Authoritative;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Internal.Game.Roping;

internal sealed class RopeCoupleController
{
    /// <summary>How long a fresh death counts as undecided: the game rules on the death screen
    /// a moment after the body goes limp, and the rope must not be cut before it has.</summary>
    private const float DeathDecisionGraceSeconds = 2f;

    internal const string ToggleKeyName = "L";
    private static readonly Key ToggleKey = System.Enum.Parse<Key>(ToggleKeyName);

    private readonly NetworkManager _network;
    private readonly RuntimeState _state;

    // Hard teardown is broadcast once per episode to avoid reliable-packet spam.
    private bool _ropeHardTornDown;
    private bool _partnerWasFalling;
    private bool _practiceAttachRefused;
    private float _deadSince = -1f;
    private readonly List<PracticePartner.LinkCandidate> _linkCandidates = new();

    internal RopeCoupleController(NetworkManager network, RuntimeState state)
    {
        _network = network;
        _state = state ?? throw new System.ArgumentNullException(nameof(state));
    }

    internal void Tick()
    {
        if (_network == null)
        {
            LeaderProtectionMarker.Hide();
            return;
        }

        // Teardown must remain reachable outside InGame.
        if (!TickRopeSafety() || _state.LocalPlayerState != PlayerState.InGame)
        {
            if (!TeamAnchorSession.IsLinked) LeaderProtectionMarker.Hide();
            return;
        }

        if (!InputCaptureState.IsKeyboardCaptured)
            HandleRopeClipInput();
        TeamAnchorSession.ReconcileLink();
        TickRopeTeam();
        TickUnanchoredFall();
        if (!TeamAnchorSession.IsLinked) LeaderProtectionMarker.Hide();
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

        if (!dead) _deadSince = -1f;
        else if (_deadSince < 0f) _deadSince = Time.unscaledTime;
        // A climber down and waiting for help has not left the rope: Cairn's own rope teams
        // carry a corpse, and the partner pays for it in endurance. Only a death that plays
        // out breaks the link.
        bool downed = dead && (DeathScreenPatch.IsHoldingDeath
                               || Time.unscaledTime - _deadSince < DeathDecisionGraceSeconds);

        bool hardUnsafe = !connected || (dead && !downed) || gameOver || atMainMenu;

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

        // The body keeps its link but not its anchors: a dead pawn is no harness to hang
        // the rope from. The partner's side still holds it.
        if ((downed || !inGame) && RopeInterop.HasRopeTeamAnchors)
            RopeInterop.ReleaseAllAnchors();
        return inGame && !downed;
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
        var keyboardPressed = keyboard != null && keyboard[ToggleKey].wasPressedThisFrame;
        if (!keyboardPressed && !RightStickLinkPressed())
            return;

        // An existing rope must always be releasable, including at zero slack.
        // PartnerOf returns -1 when this climber has no partner.
        int self = _network.LocalPlayerId;
        int linkedPartner = RopeLinkState.PartnerOf(self);
        if (linkedPartner != -1)
        {
            ReleaseClip(self, linkedPartner);
            ModLog.Info($"[RopeCouple] Unclip from player {linkedPartner}");
            return;
        }

        if (!LocalPlayerInterop.TryGetPose(out var localPos, out _))
            return;

        _linkCandidates.Clear();
        foreach (var kv in _network.RemotePlayers)
        {
            var rp = kv.Value;
            if (rp == null || rp.State != PlayerState.InGame) continue;
            var d = Vector3.Distance(localPos, new Vector3(rp.X, rp.Y, rp.Z));
            _linkCandidates.Add(new PracticePartner.LinkCandidate(kv.Key, d, PracticePartner.Is(kv.Key)));
        }

        int bestId = PracticePartner.ChooseLinkTarget(_linkCandidates, RopeRequests.LinkRangeMeters);
        float bestDist = float.MaxValue;
        for (var i = 0; i < _linkCandidates.Count; i++)
        {
            if (_linkCandidates[i].Id != bestId) continue;
            bestDist = _linkCandidates[i].Distance;
            break;
        }

        if (bestId < 0)
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
            ModLog.Info($"[RopeCouple] Link pressed — no eligible ghost within {RopeRequests.LinkRangeMeters:F0}m " +
                $"(remote players: {total}):{(total == 0 ? " none" : sb.ToString())}");
            return;
        }

        if (RopeLinkState.IsLinked(self, bestId))
        {
            ReleaseClip(self, bestId);
            ModLog.Info($"[RopeCouple] Unclip from player {bestId}");
            return;
        }

        int current = RopeLinkState.PartnerOf(self);
        if (current != -1 && current != bestId)
            ReleaseClip(self, current);

        _network.SendRopeClip(bestId, true);
        ModLog.Info($"[RopeCouple] Clip to player {bestId} at {bestDist:F1}m");
    }

    /// <summary>
    /// A bare right-stick click ties or unties. The same click with View/Share held is the
    /// emergency input release, so that chord leaves the rope alone.
    /// </summary>
    private static bool RightStickLinkPressed()
    {
        var pad = Gamepad.current;
        return pad != null && !pad.selectButton.isPressed && pad.rightStickButton.wasPressedThisFrame;
    }

    private void ReleaseClip(int self, int partner)
    {
        _network.SendRopeClip(partner, false);
        RopeLinkState.Apply(self, partner, false);
        RopeInterop.ReleaseAllAnchors();
    }

    private void TickRopeTeam()
    {
        int self = _network.LocalPlayerId;
        int partner = RopeLinkState.PartnerOf(self);

        if (partner == -1 || !_network.RemotePlayers.TryGetValue(partner, out var remote)
            || remote == null || remote.State != PlayerState.InGame
            || !RemotePlayerManager.TryGetGhostHarness(partner, out var partnerAnchor))
        {
            if (RopeInterop.HasRopeTeamAnchors) RopeInterop.ReleaseAllAnchors();
            return;
        }

        if (!RopeInterop.UpdateRopeTeamAnchor(partner, partnerAnchor))
        {
            // A refused native rope must not wipe the practice chain. Real partners still untie.
            if (PracticePartner.Is(partner))
            {
                if (_practiceAttachRefused) return;
                _practiceAttachRefused = true;
                ModLog.Warning("[RopeCouple] Practice rope attachment was refused; the link stays.");
                return;
            }

            _practiceAttachRefused = false;
            ReleaseClip(self, partner);
            ModLog.Warning("[RopeCouple] Rope attachment was refused; the link has been released.");
            return;
        }

        _practiceAttachRefused = false;
    }

    private void TickUnanchoredFall()
    {
        if (!TeamAnchorSession.IsLinked || TeamAnchorSession.HasProtection)
        {
            _partnerWasFalling = false;
            return;
        }

        var partner = RopeLinkState.PartnerOf(_network.LocalPlayerId);
        if (partner == -1 || !_network.RemotePlayers.TryGetValue(partner, out var remote)
            || remote == null || !remote.HasPlayerFrame)
        {
            _partnerWasFalling = false;
            return;
        }

        var falling = PawnCaptureInterop.GetPawnStateFromFrame(remote.PlayerFrame)
            == NetFrame.PawnStateType.Falling;
        if (falling && !_partnerWasFalling)
            RopeInterop.FallUnsecuredWithPartner();
        _partnerWasFalling = falling;
    }

    internal void ClearLinks()
    {
        RopeLinkState.Clear();
        TeamAnchorSession.Clear();
        RopeInterop.ReleaseAllAnchors();
        _ropeHardTornDown = false;
        _partnerWasFalling = false;
    }

    /// <summary>Logical links intentionally survive scene streaming and clear only on disconnect.</summary>
    internal void Reset() { }
}
