using System;
using System.Collections.Generic;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features;

internal sealed partial class ManualBelayFeature
{
    private sealed class QueuedInvitation
    {
        internal int Target, Epoch, LocalPlayer, Host;
        internal bool Asking;
        internal float Until, NextCheck, NextHud, NextLog;
        internal string Reason = "Waiting for the dialogue to finish; no invitation has been sent.";
        internal string LoggedReason;
    }
    private QueuedInvitation _queuedInvitation;
    private int _invitationEpoch;

    private bool InvitationGameplayReady => Game.State.IsLocalPlayerInGame && !Game.State.IsLocalPlayerInCutscene
        && Game.Input.IsWindowFocused && !Game.Input.IsPauseMenuActive && !Game.Time.IsPaused;

    private int InvitationHost()
    {
        foreach (var player in Players) if (player.IsHost) return player.Id;
        return -1;
    }

    private bool InvitationTargetPresent(int target)
    {
        foreach (var player in Players) if (!player.IsLocal && player.Id == target) return true;
        return false;
    }

    private void QueueInvitation(int target, bool asking)
    {
        var now = Game.Time.UnscaledTime;
        if (!float.IsFinite(now) || !Game.Belay.RecoveryCharacterAlive || !InvitationTargetPresent(target))
        { Notice("Wait for the local character and the selected partner before queuing an invitation."); return; }
        _queuedInvitation = new QueuedInvitation { Target = target, Asking = asking, Epoch = _invitationEpoch,
            LocalPlayer = LocalPlayerId, Host = InvitationHost(), Until = now + 120 };
        Game.BelayPanel.Close(); // Return dialogue input immediately; the HUD retains the queue.
        RefreshQueuedInvitationHud();
        FeatureLog.Info("[BelayInvitation] Queued locally; action=" + (asking ? "request" : "offer") + "; no network invitation sent.");
    }

    private void CancelQueuedInvitation(string reason = null)
    {
        if (_queuedInvitation == null) return;
        _queuedInvitation = null;
        Game.Hud.HideMessage("belay-invitation-queue");
        Game.Hud.HideMessage("belay-invitation-detail");
        if (!string.IsNullOrEmpty(reason))
        {
            FeatureLog.Info("[BelayInvitation] Local queue cancelled; " + reason);
            Notice(reason);
        }
    }

    private void ResetInvitationEpoch()
    {
        CancelQueuedInvitation();
        _invitationEpoch = _invitationEpoch == int.MaxValue ? 0 : _invitationEpoch + 1;
    }

    private bool HostRelaysAnotherPair()
    {
        if (!IsHost) return false;
        if (_hostSession != null) return _hostSession.Belayer != LocalPlayerId && _hostSession.Climber != LocalPlayerId;
        return _hostRecovery != null && _hostRecovery.Pair.Belayer != LocalPlayerId && _hostRecovery.Pair.Climber != LocalPlayerId;
    }

    private bool TickQueuedInvitation(float now)
    {
        if (HostRelaysAnotherPair())
        {
            CancelQueuedInvitation("Another pair is already using this room's belay session.");
            return false; // Never pause host arbitration of somebody else's pair.
        }
        var queued = _queuedInvitation;
        if (queued != null)
        {
            if (!IsBelayMode || !IsConnected || !float.IsFinite(now) || now >= queued.Until
                || queued.Epoch != _invitationEpoch || queued.LocalPlayer != LocalPlayerId || queued.Host != InvitationHost()
                || !InvitationTargetPresent(queued.Target) || !Game.Belay.RecoveryCharacterAlive)
            {
                CancelQueuedInvitation("The queued invitation expired or its character, partner or room changed.");
                Game.BelayPanel.Close(); return true;
            }
            if (!Game.Input.IsWindowFocused || Game.Input.IsPauseMenuActive || Game.Chat.IsTyping) Game.BelayPanel.Close();
            if (InvitationGameplayReady && !Game.Chat.IsTyping && !KeyboardCaptured && now >= queued.NextCheck)
            {
                queued.NextCheck = now + .5f;
                var belayer = queued.Asking ? queued.Target : LocalPlayerId;
                var climber = queued.Asking ? LocalPlayerId : queued.Target;
                var ready = TryLocations(belayer, climber, out var station, out var target, out var reason);
                if (ready)
                    ready = queued.Asking ? Game.Belay.CanClimb(out reason) : Game.Belay.CanBeginStation(out reason);
                if (ready)
                {
                    // Only a local climber can declare real capacity before an
                    // invitation. A remote climber still declares theirs on Accept.
                    var capacity = queued.Asking ? Game.Belay.RopeCapacity : MaximumRopeCapacity;
                    if (!ValidCapacity(capacity)) { ready = false; reason = "Waiting for the current climber rope capacity."; }
                    else if (!WithinRopeRange(station, target, capacity))
                    { ready = false; reason = "The current partner positions are outside the invitation's rope reach."; }
                }
                if (ready)
                {
                    var command = (queued.Asking ? "request " : "offer ") + queued.Target;
                    CancelQueuedInvitation(); // Consume before normal dispatch/callbacks; never retry a host rejection.
                    FeatureLog.Info("[BelayInvitation] Dialogue queue dispatching once through normal invitation validation.");
                    HandleChat(command);
                    return true;
                }
                queued.Reason = string.IsNullOrWhiteSpace(reason) ? "Waiting for fresh gameplay readiness." : reason;
                if (queued.Reason.Length > 240) queued.Reason = queued.Reason[..240];
                if (queued.LoggedReason != queued.Reason && now >= queued.NextLog)
                {
                    queued.LoggedReason = queued.Reason; queued.NextLog = now + 2;
                    FeatureLog.Info("[BelayInvitation] Waiting locally; " + queued.Reason);
                }
            }
            if (now >= queued.NextHud) { queued.NextHud = now + .5f; RefreshQueuedInvitationHud(); }
            if (Game.BelayPanel.IsOpen && now >= _nextPanelRefresh) { _nextPanelRefresh = now + .2f; RefreshPanel(); }
            return true;
        }
        if (IsBelayMode && IsConnected && Game.State.IsLocalPlayerInCutscene
            && _active == null && _offer == null && _offeredTarget < 0 && _recovery == null)
        {
            if (!Game.Input.IsWindowFocused || Game.Input.IsPauseMenuActive || Game.Chat.IsTyping)
                Game.BelayPanel.Close();
            else if (Game.BelayPanel.IsOpen && now >= _nextPanelRefresh)
            { _nextPanelRefresh = now + .2f; RefreshPanel(); }
            return true; // Keep the metadata-only L panel available during dialogue.
        }
        return false;
    }

    private bool RefreshInvitationQueuePanel()
    {
        if (_queuedInvitation != null)
        {
            var queued = _queuedInvitation;
            Game.BelayPanel.SetView(new BelayPanelView { Phase = BelayPanelPhase.Waiting, AwaitingGameplay = true,
                LocalBelayer = !queued.Asking, PartnerName = GetPlayerName(queued.Target),
                Description = "AGUARDANDO DIÁLOGO / INVITATION QUEUED — NOT SENT\n" + queued.Reason
                    + "\nClose keeps waiting; Backspace cancels." });
            return true;
        }
        if (!Game.State.IsLocalPlayerInCutscene || _active != null || _recovery != null) return false;
        if (_offer != null || _offeredTarget >= 0)
        {
            Game.BelayPanel.SetView(new BelayPanelView { Phase = BelayPanelPhase.Waiting, AwaitingGameplay = true,
                Description = "Wait for active gameplay before accepting or connecting a native rope." });
            return true;
        }
        var partners = new List<BelayPartnerRow>();
        foreach (var player in Players)
            if (!player.IsLocal) partners.Add(new BelayPartnerRow(player.Id, player.Name, float.NaN));
        Game.BelayPanel.SetView(new BelayPanelView { Phase = BelayPanelPhase.Partners, AwaitingGameplay = true,
            Partners = partners, CanOffer = partners.Count > 0, CanRequest = partners.Count > 0,
            Description = "O/I queues an invitation locally until the dialogue ends. Distance and native rope readiness are checked afterward." });
        return true;
    }

    private void RefreshQueuedInvitationHud()
    {
        var queued = _queuedInvitation;
        if (queued == null) return;
        Game.Hud.ShowMessage("belay-invitation-queue", "AGUARDANDO DIÁLOGO / INVITATION QUEUED — " + GetPlayerName(queued.Target), 1.2f);
        Game.Hud.ShowMessage("belay-invitation-detail", "Ainda não enviado / Not sent yet. " + queued.Reason, 1.2f);
    }
}
