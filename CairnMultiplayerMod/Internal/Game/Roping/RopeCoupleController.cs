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
    private bool _unclippedForDeath;
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
            RopeClipPrompt.Hide();
            return;
        }

        // Teardown must remain reachable outside InGame.
        if (!TickRopeSafety() || _state.LocalPlayerState != PlayerState.InGame)
        {
            if (!TeamAnchorSession.IsLinked) LeaderProtectionMarker.Hide();
            RopeClipPrompt.Hide();
            return;
        }

        if (!InputCaptureState.IsKeyboardCaptured)
            HandleRopeClipInput();
        TeamAnchorSession.ReconcileLink();
        TickRopeTeam();
        TickUnanchoredFall();
        RopeInterop.TickPartnerFall(_network);
        TickRopePrompt();
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

        if (!dead)
        {
            _deadSince = -1f;
            _unclippedForDeath = false;
            // A climber back on their feet can die again. Leave the flag set through a
            // disconnect or the menu, or the teardown would repeat every frame.
            if (connected && !gameOver && !atMainMenu)
                _ropeHardTornDown = false;
        }
        else if (_deadSince < 0f)
        {
            _deadSince = Time.unscaledTime;
        }

        // The grace is the moment before the death screen has been held or opened. A held
        // death unclips once, as if this climber pressed the rope control. A death that
        // plays out still takes the hard teardown.
        var withinGrace = dead && Time.unscaledTime - _deadSince < DeathDecisionGraceSeconds;
        var action = RopeSafety.Decide(
            connected,
            inGame,
            dead,
            DeathScreenPatch.IsHoldingDeath,
            withinGrace,
            gameOver,
            atMainMenu,
            _unclippedForDeath,
            _ropeHardTornDown);

        switch (action)
        {
            case RopeSafetyAction.TearDown:
                if (RopeInterop.HasRopeTeamAnchors)
                    RopeInterop.ReleaseAllAnchors();
                _ropeHardTornDown = true;
                TearDownAllLocalRopeLinks(!connected ? "disconnected"
                    : dead ? "local player died"
                    : gameOver ? "game over"
                    : "returned to main menu");
                return false;

            case RopeSafetyAction.UnclipOnce:
                _unclippedForDeath = true;
                _ropeHardTornDown = false;
                UnclipHeldDeath();
                return false;

            case RopeSafetyAction.ReleaseAnchors:
                // Leave _ropeHardTornDown as it is. Clearing it here made the next hard
                // frame tear the rope down again.
                if (RopeInterop.HasRopeTeamAnchors)
                    RopeInterop.ReleaseAllAnchors();
                return false;

            default:
                _ropeHardTornDown = false;
                return true;
        }
    }

    /// <summary>One voluntary unclip. A planted staff holds the chain. Anything else clears it.</summary>
    private void UnclipHeldDeath()
    {
        int self = _network.LocalPlayerId;
        var partners = new List<int>();
        foreach (var (a, b) in RopeLinkState.Links())
        {
            if (a == self) partners.Add(b);
            else if (b == self) partners.Add(a);
        }

        if (partners.Count == 0) return;
        foreach (var partner in partners)
            ReleaseClip(self, partner);
        ModLog.Info($"[RopeCouple] Unclipped {partners.Count} link(s): a climber died");
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
        if (canNotify)
        {
            TopAnchorSession.RequestDrop();
            RopeInterop.SendTopAnchorDrop(_network);
        }

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
        if (!keyboardPressed && !RightStickLinkPressed()
            && !ModControllerInput.WasPressed(ControllerShortcut.ToggleRope))
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

        if (TeamAnchorSession.IsLinked && TeamAnchorSession.LeaderLeft && TeamAnchorSession.IsLocalFollower)
        {
            ModLog.Info("[RopeCouple] Link pressed — the staff is holding the pitch.");
            return;
        }

        if (!_network.TryGetLinkPosition(_network.LocalPlayerId, out var originX, out var originY, out var originZ))
        {
            ModLog.Info("[RopeCouple] Link pressed — this climber has no stored in-game pose yet.");
            return;
        }

        var localPos = new Vector3(originX, originY, originZ);
        FillLinkCandidates(localPos);
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
                if (!_network.TryGetLinkPosition(kv.Key, out var x, out var y, out var z))
                {
                    sb.Append($" [{kv.Key} state={rp.State} stored=no]");
                    continue;
                }
                float d = Vector3.Distance(localPos, new Vector3(x, y, z));
                sb.Append($" [{kv.Key} state={rp.State} dist={d:F1}m stored]");
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
    /// One row for both presses. Clip-in uses the same reach test as the key. Cut rope
    /// follows the grip drain, which only the leader has while the follower hangs.
    /// </summary>
    private void TickRopePrompt()
    {
        var inputAvailable = !InputCaptureState.IsKeyboardCaptured;
        if (RopeCutPromptPolicy.ShouldShow(RopeInterop.GripDraining, inputAvailable))
        {
            RopeClipPrompt.Tick(true, RopeClipPrompt.CutLabel);
            return;
        }

        var linked = RopeLinkState.PartnerOf(_network.LocalPlayerId) != -1;
        var pitchHeld = TeamAnchorSession.IsLinked
            && TeamAnchorSession.LeaderLeft
            && TeamAnchorSession.IsLocalFollower;
        RopeClipPrompt.Tick(
            RopeClipPromptPolicy.ShouldShow(
                linked,
                !linked && HasClipTargetInRange(),
                inputAvailable,
                pitchHeld),
            RopeClipPrompt.Label);
    }

    private bool HasClipTargetInRange()
    {
        if (!_network.TryGetLinkPosition(_network.LocalPlayerId, out var x, out var y, out var z))
            return false;
        FillLinkCandidates(new Vector3(x, y, z));
        return PracticePartner.ChooseLinkTarget(_linkCandidates, RopeRequests.LinkRangeMeters) >= 0;
    }

    private void FillLinkCandidates(Vector3 localPos)
    {
        _linkCandidates.Clear();
        foreach (var kv in _network.RemotePlayers)
        {
            if (!_network.TryGetLinkPosition(kv.Key, out var x, out var y, out var z)) continue;
            var d = Vector3.Distance(localPos, new Vector3(x, y, z));
            _linkCandidates.Add(new PracticePartner.LinkCandidate(kv.Key, d, PracticePartner.Is(kv.Key)));
        }
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
        var untie = RopeInterop.PrepareUntie(self, partner, TeamAnchorSession.IsLocalLeader, localUntied: true);
        if (untie.HoldStaff && TeamAnchorSession.IsLocalLeader && TopAnchorSession.HasStick)
        {
            TopAnchorSession.RequestHold();
            // A client does not apply its own unclip until the host echoes it.
            // The link drops below, and the same frame reconciles. Commit the
            // hold first so that reconcile leaves the chain for the follower.
            TeamAnchorSession.HoldForFollower();
            RopeInterop.SendTopAnchorLeave(_network);
        }

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
        RopeClipPrompt.Hide();
        RopeLinkState.Clear();
        TeamAnchorSession.Clear();
        TeamAnchorSession.DisarmRules();
        RopeInterop.ReleaseAllAnchors();
        RopeInterop.ResetPartnerFall();
        _ropeHardTornDown = false;
        _partnerWasFalling = false;
    }

    /// <summary>Logical links intentionally survive scene streaming and clear only on disconnect.</summary>
    internal void Reset()
    {
        RopeClipPrompt.Hide();
    }
}
