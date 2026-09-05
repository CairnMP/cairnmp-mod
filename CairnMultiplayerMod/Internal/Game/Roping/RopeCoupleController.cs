using CairnMultiplayerMod.Internal.Diagnostics;
using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Game.Lifecycle;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayerMod.Internal.Game.Scenes;
using CairnMultiplayerMod.Internal.Networking;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Internal.Game.Roping;

/// <summary>
/// Co-op roping, ticked every frame: the E input (re)toggles the link intent
/// (ClientRopeClip, relayed by the host), then we maintain the NATIVE rope team (the
/// local lifeline's rope clipped to a mobile piton placed on the partner, a system
/// carried over from Episure, cf. RopeInterop.UpdateRopeTeamAnchor).
/// </summary>
internal sealed class RopeCoupleController
{
    private const float RopeClipRangeMeters = 3f;

    private readonly NetworkManager _network;
    private readonly RuntimeState _state;

    // Anti-spam: we broadcast the "hard" unclip (death, game over, menu, disconnect) only
    // once per episode. Re-armed as soon as we leave the dangerous state.
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

        // Roping safety: ALWAYS runs (even outside InGame) to guarantee the teardown of
        // links/anchors in as many situations as possible (death, game over, menu,
        // disconnect, loading, bivouac, partner left).
        TickRopeSafety();

        if (_state.LocalPlayerState != PlayerState.InGame)
            return;

        if (!CairnMultiplayerMod.Internal.Game.Input.InputCaptureState.IsKeyboardCaptured)
            HandleRopeClipInput();
        TickRopeTeam();
    }

    /// <summary>
    /// Safety net of the roping system, evaluated every frame. Classifies the local state:
    ///  - HARD (death, game over, return to main menu, disconnect): we BREAK the logical link
    ///    and notify the partner(s) (reliable unclip broadcast), then release the native
    ///    anchors. Broadcast only once per episode (flag _ropeHardTornDown).
    ///  - SOFT (loading / scene streaming / bivouac, i.e. any transient non-InGame state):
    ///    we KEEP the logical link (it must survive transitions) but release the native
    ///    anchors — they'll be recreated when back InGame by TickRopeTeam.
    /// The partner who dies/leaves broadcasts their own unclip (or OnPlayerLeft removes it), so
    /// each side cleans up its own state: no need to detect the remote death here.
    /// Note: the MP pause is NOT a hard case (the scene stays a gameplay scene, not MainMenu).
    /// </summary>
    private void TickRopeSafety()
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
            return;
        }

        // Left the dangerous state -> re-arm the broadcast for the next episode.
        _ropeHardTornDown = false;

        // Transient state (loading / bivouac): keep the link, just release the native
        // anchors so we don't leave a rope pinned to an object being destroyed.
        if (!inGame && RopeInterop.HasRopeTeamAnchors)
            RopeInterop.ReleaseAllAnchors();
    }

    /// <summary>
    /// Breaks all rope links involving the local player: broadcasts a reliable unclip to each
    /// partner (if the network still responds) then removes the link locally right away
    /// (client side, we don't wait for the host's echo). Idempotent.
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

        ModLog.Info($"[RopeCouple] Auto-unclipped {partners.Count} link(s): {reason}");
    }

    /// <summary>
    /// E input: (re)toggles a rope link with the nearest ghost. We only send the intent;
    /// RopeLinkState manages the authoritative link state. The native rope team (rope +
    /// belay) is maintained by TickRopeTeam, not here.
    /// </summary>
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

        // Nearest InGame ghost within range. Note: we keep a dedicated flag rather than a
        // negative sentinel on bestId — the debug mirror has a negative id (-777) that would
        // collide with a "-1 = none" sentinel.
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
            // Diagnostic: why is nothing happening? Dump id + State + distance of each remote.
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

        // Already roped to this ghost -> unclip.
        if (RopeLinkState.IsLinked(self, bestId))
        {
            _network.SendRopeClip(bestId, false);
            ModLog.Info($"[RopeCouple] Unclip from player {bestId}");
            return;
        }

        // Clip (v1: one link per player -> drop any current partner).
        int current = RopeLinkState.PartnerOf(self);
        if (current >= 0 && current != bestId)
            _network.SendRopeClip(current, false);
        _network.SendRopeClip(bestId, true);
        ModLog.Info($"[RopeCouple] Clip to player {bestId} at {bestDist:F1}m");
    }

    /// <summary>
    /// NATIVE rope team (Episure system), every frame. As long as a partner is roped, we
    /// maintain an anchor (cloned piton, without the placement sound) placed on the partner's
    /// harness and clip the local lifeline's rope to it (UpdateRopeTeamAnchor). The native rope
    /// then serves both as a VISUAL (both clients see their own rope, it's symmetric) and as a
    /// BELAY (a fall is caught natively: hanging, no death or drain). Without a partner, we
    /// release it. Everything comes from already-synchronized positions -> nothing new to send.
    /// </summary>
    private void TickRopeTeam()
    {
        int self = _network.LocalPlayerId;
        int partner = RopeLinkState.PartnerOf(self);

        if (partner < 0 || !RemotePlayerManager.TryGetGhostHarnessAttachPosition(partner, out var partnerAnchor))
        {
            if (RopeInterop.HasRopeTeamAnchors) RopeInterop.ReleaseAllAnchors();
            return;
        }

        RopeInterop.UpdateRopeTeamAnchor(partner, partnerAnchor);
    }

    /// <summary>Clears all rope links + their ropes (disconnect / return to menu).</summary>
    internal void ClearLinks()
    {
        RopeLinkState.Clear();
        RopeInterop.ReleaseAllAnchors();
        _ropeHardTornDown = false;
    }

    /// <summary>
    /// Local reset on scene change (called by the player broadcaster). No-op for roping:
    /// the link is global (RopeLinkState) and persists across scene streaming; cleanup
    /// happens on disconnect via ClearLinks.
    /// </summary>
    internal void Reset() { }
}
