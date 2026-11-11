using System;
using System.Collections.Generic;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Players;
using CairnMultiplayerMod.Internal.Networking;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Internal.Game.Roping;

internal static partial class RopeInterop
{
    private static Il2Cpp.Lifeline _lifelineCached;
    private static int _lastLifelineSearchFrame;
    private static readonly PitonSync _pitons = new();
    private static readonly List<PlacedPiton> _placedPitons = new();
    private static readonly List<ServerPitonPlaced> LinkReplay = new();
    private static readonly HashSet<IntPtr> _pitonPointers = new();
    private static readonly HashSet<IntPtr> _pitonPointersAfterSpawn = new();
    // A refused native detach is tried again, but not every frame.
    private static readonly Dictionary<IntPtr, float> _detachRetryAt = new();
    private const float DetachRetrySeconds = 2f;
    private static float _lastPitonCheckErrorLogAt;
    private const float PitonCheckErrorLogIntervalSeconds = 5f;
    private static Il2Cpp.ClimbingV2PawnController _localClimbControllerCached;
    private static readonly List<RecoveryTarget> _recoveryTargets = new();
    private static readonly List<PitonRecoveryPolicy.Candidate> _recoveryCandidates = new();
    private static readonly List<RecoveryTarget> _partnerTargets = new();
    private static readonly List<PitonRecoveryPolicy.Candidate> _partnerCandidates = new();
    private static readonly HashSet<IntPtr> _recoverySeen = new();
    private static readonly List<IntPtr> _unclippedPointers = new();
    private static readonly List<uint> _teamDetachIds = new();
    private static readonly List<uint> _teamReattachIds = new();
    private static readonly Key TakeKey = Enum.Parse<Key>(PitonRecoverySession.TakeKeyName);
    private static float _dpadUpPressedAt = -1f;
    private static readonly Dictionary<uint, float> _teamDetachRetryAt = new();
    private static readonly List<uint> _teamChainIds = new();
    private static readonly HashSet<uint> _anchorMissLogged = new();
    private static readonly HashSet<uint> _quickdrawMissLogged = new();
    // DetachPiton on a higher piton must leave the quickdraw in the rock. If it
    // does not, later pitons stay on the rope so they can still be taken.
    private static bool _unclipClearsQuickdraw;
    private static readonly List<TeamAnchorSession.ChainRelease> _releaseIds = new();
    // Untie detaches first. The placed list may drop the piton on a later frame.
    // Remember it until that frame, or the drop is announced as a take.
    private static readonly List<IntPtr> _rememberUnclipped = new();
    private static readonly Dictionary<(bool Local, uint Id), float> _releaseRetryAt = new();
    private static bool _releaseAnnounced;
    private static ushort _pitonPoolChannel;
    private static int _nextPitonPoolId;
    private static int _pitonPoolId;
    private static int _appliedPitonPoolId;
    private static bool _heldPool;
    private static int _heldPoolFrom;
    private static int _heldPoolLink;
    private static int _heldPoolCount;
    private static bool _rackFormed;
    private static NetworkManager _poolNetwork;

    private struct RecoveryTarget
    {
        internal Il2Cpp.Piton Piton;
        internal PitonRecoveryPolicy.Candidate Candidate;
    }

    internal static void ResetCaches()
    {
        // Scene callbacks may arrive after native objects were destroyed; never invoke
        // native removal through references belonging to the previous scene here. Piton
        // identities are not dropped either: whether the Lifeline survived is only known
        // once it resolves again, and PitonSync decides it then.
        _lifelineCached = null;
        _lastLifelineSearchFrame = 0;
        _detachRetryAt.Clear();
        _releaseRetryAt.Clear();
        _rememberUnclipped.Clear();
        _teamDetachRetryAt.Clear();
        _anchorMissLogged.Clear();
        _quickdrawMissLogged.Clear();
        _unclipClearsQuickdraw = false;
        _localClimbControllerCached = null;
    }

    /// <summary>Leaving the session: the other climbers' pitons come off this Lifeline.</summary>
    internal static void ClearRemotePitons()
    {
        _rackFormed = false;
        _poolNetwork = null;
        try
        {
            var lifeline = TryGetLifeline();
            if (lifeline != null && TryCollectPitonPointers(lifeline, _pitonPointers))
            {
                foreach (var pointer in new List<IntPtr>(_pitons.RemotePointers))
                    if (_pitonPointers.Contains(pointer)) TryDetachPitonViaLifeline(pointer);
            }
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.clear-remote-pitons", exception);
        }
        _pitons.ForgetSession();
        _detachRetryAt.Clear();
    }

    /// <summary>The host announced a piton. It is created on the next frame of play, so one
    /// announced during a load or a camp is not lost.</summary>
    internal static void KnowRemotePiton(ServerPitonPlaced placed) => _pitons.Know(placed);

    internal static void ForgetRemotePiton(uint pitonId) => _pitons.Forget(pitonId);

    /// <summary>Another climber took up one of our pitons.</summary>
    internal static void ForgetOwnPiton(uint localId) => _pitons.TakenByOther(localId);

    public static Il2Cpp.Lifeline TryGetLifeline()
    {
        if (_lifelineCached != null) return _lifelineCached;
        if (_lastLifelineSearchFrame != 0 && Time.frameCount - _lastLifelineSearchFrame < 30) return null;
        _lastLifelineSearchFrame = Time.frameCount;

        try
        {
            var mcGameObject = Players.LocalPlayerInterop.TryGetMCGameObject();
            _lifelineCached = mcGameObject?.GetComponent<Il2Cpp.Lifeline>()
                              ?? mcGameObject?.GetComponentInChildren<Il2Cpp.Lifeline>(true);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.resolve-lifeline", exception);
        }

        return _lifelineCached;
    }

    /// <summary>
    /// One pass per frame of play: read what the Lifeline holds, let PitonSync decide, carry
    /// out the native part and tell the host the rest.
    /// </summary>
    internal static void SyncPitons(NetworkManager network)
    {
        var lifeline = TryGetLifeline();
        if (lifeline == null || network == null)
        {
            PitonRecoverySession.HideTip();
            RopeLengthReadout.Hide();
            return;
        }

        try
        {
            double now = Time.unscaledTime;
            TeamAnchorSession.SetLocalPlayer(network.LocalPlayerId);
            RopeLengthReadout.Tick();
            _pitons.OnLifeline(lifeline.Pointer, now);
            ReleaseUnhookedPitons(lifeline);
            TickPitonRecovery(lifeline, network);
            if (!TryCollectPlacedPitons(lifeline))
            {
                TickTopAnchor(network);
                return;
            }

            System.Numerics.Vector3? localPosition = null;
            if (Players.LocalPlayerInterop.TryGetPose(out var pose, out _))
                localPosition = new System.Numerics.Vector3(pose.x, pose.y, pose.z);

            _pitons.SpawnReplicas = TeamAnchorSession.RulesArmed;
            var actions = _pitons.Reconcile(_placedPitons, localPosition, now);
            if (!actions.IsEmpty)
            {
                foreach (var pointer in actions.Strip) StripWhenAllowed(pointer);
                foreach (var pointer in actions.Detach) DetachWhenAllowed(pointer);
                foreach (var placed in actions.Spawn) SpawnRemotePiton(lifeline, placed, now);
                foreach (var (localId, pointer) in actions.Announce) AnnounceLocalPiton(network, localId, pointer);
                foreach (var localId in actions.Removed)
                {
                    network.SendPitonRemoved(localId);
                    TeamAnchorSession.NoteLocalRemoval(localId);
                }
                foreach (var pitonId in actions.Recovered)
                {
                    network.SendPitonRecovered(pitonId);
                    TeamAnchorSession.NoteAuthoritativeRemoval(pitonId);
                    ModLog.Debug($"[Piton] Took up remote piton #{pitonId}");
                }
            }

            ApplyFollowerAnchorRope();
            RememberUnclippedPitons();
            TickTopAnchor(network);
        }
        catch (Exception ex)
        {
            // Rate-limited: the list can be transiently inconsistent (reload, pickup),
            // no point drowning the log + weighing down the frame with IL2CPP stack traces.
            var now = Time.unscaledTime;
            if (now - _lastPitonCheckErrorLogAt >= PitonCheckErrorLogIntervalSeconds)
            {
                _lastPitonCheckErrorLogAt = now;
                ModLog.Warning($"[Piton] Sync failed (rate-limited): {ex.GetType().Name}: {GameInterop.FirstLine(ex.Message)}");
            }
        }
    }

    /// <summary>
    /// Offers the nearest placed piton. A take happens before the reconcile, so the same frame
    /// tells the session it left the wall. Climbot's bivouac repair is not involved.
    /// </summary>
    private static void TickPitonRecovery(Il2Cpp.Lifeline lifeline, NetworkManager network)
    {
        if (!PitonRecoverySession.SuppressesClimbot
            || InputCaptureState.IsKeyboardCaptured
            || MultiplayerPausePatch.IsPauseMenuActive)
        {
            _dpadUpPressedAt = -1f;
            PitonRecoverySession.HideTip();
            return;
        }

        var dpadUpTapped = DpadUpTapped();
        if (!TryGetClimberPosition(out var origin))
        {
            PitonRecoverySession.HideTip();
            return;
        }

        var pose = ReadLocal().Status;

        var havePartner = TryGetPracticeFollowerPosition(network, out var partnerOrigin);
        _recoveryTargets.Clear();
        _recoveryCandidates.Clear();
        _partnerTargets.Clear();
        _partnerCandidates.Clear();
        try
        {
            var placed = lifeline.PlacedPitons;
            if (placed != null)
            {
                for (var i = 0; i < placed.Count; i++)
                    OfferPlacedPiton(lifeline, placed[i]?.Piton, pose, havePartner, partnerOrigin);
            }

            // A higher piton leaves the placed list when it is unclipped. The hardware
            // is still in the rock, and it is still a take.
            _pitons.CopyUnclipped(_unclippedPointers);
            for (var i = 0; i < _unclippedPointers.Count; i++)
            {
                try
                {
                    OfferPlacedPiton(lifeline, new Il2Cpp.Piton(_unclippedPointers[i]), pose, havePartner, partnerOrigin);
                }
                catch (Exception exception)
                {
                    ModLog.SuppressedException("rope.piton-recovery-scan", exception);
                }
            }

            var partnerOffer = false;
            var choice = new PitonRecoveryPolicy.Result(PitonRecoveryPolicy.Decision.None, -1);
            if (havePartner && _partnerCandidates.Count > 0)
            {
                choice = PitonRecoveryPolicy.Choose(_partnerCandidates, partnerOrigin);
                partnerOffer = choice.Decision != PitonRecoveryPolicy.Decision.None;
            }

            if (!partnerOffer)
                choice = PitonRecoveryPolicy.Choose(_recoveryCandidates, origin);
            var targets = partnerOffer ? _partnerTargets : _recoveryTargets;
            switch (choice.Decision)
            {
                case PitonRecoveryPolicy.Decision.Take:
                    PitonRecoverySession.ShowTip(partnerOffer
                        ? PitonRecoverySession.PartnerTakeText
                        : PitonRecoverySession.TakeText);
                    if (RecoveryPressed(dpadUpTapped))
                        TryTakePiton(lifeline, network, targets[choice.Index].Piton, partnerOffer);
                    break;
                case PitonRecoveryPolicy.Decision.BagFull:
                    RopeClipPrompt.Hide(PitonRecoverySession.TakeId);
                    PitonRecoverySession.Show(PitonRecoverySession.BagFullText);
                    break;
                default:
                    PitonRecoverySession.HideTip();
                    break;
            }
        }
        finally
        {
            _recoveryTargets.Clear();
            _recoveryCandidates.Clear();
            _partnerTargets.Clear();
            _partnerCandidates.Clear();
            _recoverySeen.Clear();
            _unclippedPointers.Clear();
        }
    }

    /// <summary>One placed piton, or a chain piton kept in the rock after it left the rope.</summary>
    private static void OfferPlacedPiton(
        Il2Cpp.Lifeline lifeline,
        Il2Cpp.Piton piton,
        PartnerPose pose,
        bool havePartner,
        System.Numerics.Vector3 partnerOrigin)
    {
        if (piton == null || piton.Pointer == IntPtr.Zero || piton.ItemId.value == 0) return;
        if (!_recoverySeen.Add(piton.Pointer)) return;
        if (IsOffBelay(lifeline, piton) || !TryGetPitonPosition(piton, out var position)) return;
        var held = false;
        if (pose == PartnerPose.Hanging)
        {
            try
            {
                held = lifeline.IsPitonCriticalForLifeline(piton);
            }
            catch (Exception exception)
            {
                ModLog.SuppressedException("rope.piton-recovery-scan", exception);
                return;
            }
        }

        var candidate = new PitonRecoveryPolicy.Candidate(
            position, PitonRecoveryPolicy.RefusesHeldPiton(pose, held), BagCanTake(piton));
        var tracked = _pitons.TryGetId(piton.Pointer, out var trackedId, out var ownLocalId);
        var side = PitonRecoveryPolicy.Classify(
            tracked,
            tracked && TeamAnchorSession.AllowsTake(trackedId, ownLocalId),
            TopAnchorSession.OwnsPointer(piton.Pointer));
        if (side == PitonRecoveryPolicy.TakeSide.PracticeGhost)
        {
            if (!havePartner) return;
            var distance = System.Numerics.Vector3.Distance(partnerOrigin, position);
            if (!PracticePitonTake.OffersChainPiton(
                    TeamAnchorSession.IsLinked, TeamAnchorSession.IsLocalLeader,
                    TeamAnchorSession.FollowerId, true, distance))
                return;
            _partnerTargets.Add(new RecoveryTarget { Piton = piton, Candidate = candidate });
            _partnerCandidates.Add(candidate);
            return;
        }

        if (side != PitonRecoveryPolicy.TakeSide.Local) return;

        _recoveryTargets.Add(new RecoveryTarget { Piton = piton, Candidate = candidate });
        _recoveryCandidates.Add(candidate);
    }

    private static bool RecoveryPressed(bool dpadUpTapped)
    {
        var keyboard = Keyboard.current;
        return dpadUpTapped || (keyboard != null && keyboard[TakeKey].wasPressedThisFrame);
    }

    /// <summary>
    /// D-pad up places a piton when held. A short release takes one instead.
    /// View/Share and the left trigger keep their own chords on this button.
    /// </summary>
    private static bool DpadUpTapped()
    {
        var pad = Gamepad.current;
        if (pad == null)
        {
            _dpadUpPressedAt = -1f;
            return false;
        }

        var cancel = pad.selectButton.isPressed || pad.leftTrigger.ReadValue() > 0.5f;
        return PitonTakeTap.Advance(ref _dpadUpPressedAt, pad.dpad.up.isPressed, cancel,
            Time.unscaledTime, PitonTakeTap.MaxSeconds);
    }

    private static bool BagCanTake(Il2Cpp.Piton piton)
    {
        try
        {
            var manager = InventoryManager.Instance;
            return manager != null && manager.CanPutItemParts(StorageRequest(piton.ItemId));
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.piton-recovery-bag", exception);
            return false;
        }
    }

    private static InventoryStorageRequest StorageRequest(Il2Cpp.InventoryItemStringId itemId)
        => new(itemId, 1, contentItemId: default, warmForSeconds: 0, lootTarget: LootTarget.Aava,
            bagMode: BagSearchMode.BottomUp, printErrors: false, displayToasts: false);

    /// <summary>
    /// Climbot's per-piton recovery pulls the hardware off the wall into the bot.
    /// <see cref="Il2Cpp.Lifeline.RemovePiton"/> and <see cref="Il2Cpp.Lifeline.DetachPiton"/> only
    /// unclip the rope and leave the piton in the rock, so a take never calls them.
    /// </summary>
    private static void TryTakePiton(Il2Cpp.Lifeline lifeline, NetworkManager network, Il2Cpp.Piton piton, bool forPracticeFollower)
    {
        if (piton == null || piton.Pointer == IntPtr.Zero) return;
        // The staff stays in the rock. RecoverQuickDraw would put it in the bag.
        if (TopAnchorSession.OwnsPointer(piton.Pointer)) return;
        var haveClimber = forPracticeFollower
            ? TryGetPracticeFollowerPosition(network, out var climber)
            : TryGetClimberPosition(out climber);
        if (!haveClimber
            || !TryGetPitonPosition(piton, out var pitonPosition)
            || PitonRecoveryPolicy.Choose(
                new[] { new PitonRecoveryPolicy.Candidate(pitonPosition, false, true) }, climber).Decision
            != PitonRecoveryPolicy.Decision.Take)
            return;
        if (_pitons.TryGetId(piton.Pointer, out var tracked, out var ownLocalId))
        {
            if (forPracticeFollower)
            {
                if (TeamAnchorSession.AllowsTake(tracked, ownLocalId)) return;
                var distance = System.Numerics.Vector3.Distance(climber, pitonPosition);
                if (!PracticePitonTake.OffersChainPiton(
                        TeamAnchorSession.IsLinked, TeamAnchorSession.IsLocalLeader,
                        TeamAnchorSession.FollowerId, true, distance))
                    return;
            }
            else if (!TeamAnchorSession.AllowsTake(tracked, ownLocalId))
            {
                return;
            }
        }
        else if (forPracticeFollower)
        {
            return;
        }
        var quickdraw = piton.Quickdraw;
        if (quickdraw == null || quickdraw.Pointer == IntPtr.Zero)
            quickdraw = piton.includedQuickdraw;
        if (quickdraw == null || quickdraw.Pointer == IntPtr.Zero)
        {
            ModLog.Warning("[Piton] The placed piton has no quickdraw to recover");
            return;
        }

        var robot = ResolveLocalClimbot();
        var recovering = robot?.recoveringQuickdraw;
        if (recovering == null)
        {
            ModLog.Warning("[Piton] Climbot has no quickdraw recovery");
            return;
        }

        var manager = InventoryManager.Instance;
        var pointer = piton.Pointer;
        var itemId = piton.ItemId;
        var climbotBefore = CountPitons(manager, manager?.ClimbotInventoryData);
        var playerBefore = CountPitons(manager, manager?.PitonInventoryData);
        var storedBefore = CountStored(manager, itemId);
        bool recovered;
        PitonReturn.BeginTake();
        try
        {
            recovered = recovering.RecoverQuickDraw(quickdraw, showUi: false);
        }
        catch (Exception exception)
        {
            ModLog.Warning($"[Piton] RecoverQuickDraw failed: {exception.Message}");
            return;
        }
        finally
        {
            PitonReturn.EndTake();
        }

        if (!recovered || LifelineHolds(lifeline, pointer))
        {
            ModLog.Warning("[Piton] The piton stayed on the wall");
            return;
        }

        _pitons.MarkTaken(pointer);

        if (manager != null && CountPitons(manager, manager.PitonInventoryData) <= playerBefore)
        {
            if (CountPitons(manager, manager.ClimbotInventoryData) > climbotBefore)
            {
                try
                {
                    var moved = manager.TransferPitonsFromClimbotToPlayer(false, new Il2CppSystem.Nullable<int>(1));
                    if (moved < 1)
                        ModLog.Warning("[Piton] Climbot recovered a piton the backpack did not accept");
                }
                catch (Exception exception)
                {
                    ModLog.Warning($"[Piton] Could not store a recovered piton: {exception.Message}");
                }
            }
            else if (CountStored(manager, itemId) <= storedBefore)
            {
                try
                {
                    var response = manager.PutItemParts(StorageRequest(itemId));
                    if (!response.CompleteSuccess)
                        ModLog.Warning("[Piton] The wall released a piton the backpack did not accept");
                }
                catch (Exception exception)
                {
                    ModLog.Warning($"[Piton] Could not store a recovered piton: {exception.Message}");
                }
            }
        }

        DestroyReleasedPiton(piton);
    }

    private static int CountStored(InventoryManager manager, Il2Cpp.InventoryItemStringId itemId)
    {
        if (manager == null) return 0;
        try { return manager.GetItemCount(itemId, null); }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.piton-recovery-count", exception);
            return 0;
        }
    }

    internal static void BindPitonPoolChannel(ushort channel) => _pitonPoolChannel = channel;

    /// <summary>
    /// A new tie after camp has an empty local book. The host sends the committed pitons
    /// again and records the partner's on this wall. A piton already live is only updated.
    /// </summary>
    internal static void ReplayPitonsOnLink(NetworkManager network)
    {
        if (network == null) return;
        var partner = RopeLinkState.PartnerOf(network.LocalPlayerId);
        if (partner < 0) return;
        if (!network.ReplayPitonsForLink(partner, LinkReplay)) return;
        foreach (var placed in LinkReplay)
            KnowRemotePiton(placed);
    }

    /// <summary>
    /// A rope-up. The first one in a session makes the shared rack: the leader keeps
    /// at most six, and the follower's own pitons leave. After that, the follower
    /// only hands over the pitons the leader's rack still has room for.
    /// </summary>
    internal static void BeginPitonPool(NetworkManager network)
    {
        _poolNetwork = network;
        _pitonPoolId = ++_nextPitonPoolId;
        _appliedPitonPoolId = 0;
        if (!TeamAnchorSession.IsLocalFollower)
        {
            // The count may already be here. The guest becomes leader only when the clip arrives.
            if (TeamAnchorSession.IsLocalLeader) TakeHeldPool();
            return;
        }

        _heldPool = false;
        var forming = !_rackFormed;
        var count = forming ? ClearCarriedPitons() : CountCarriedPitons();
        _rackFormed = true;
        if (network == null || _pitonPoolChannel == 0)
        {
            ModLog.Warning("[RopeTeam] The follower's pitons were counted and the count was not sent");
            return;
        }

        network.SendFeatureStream(
            _pitonPoolChannel, PitonPool.WriteContribution(_pitonPoolId, count), reliable: true);
        ModLog.Info(forming
            ? $"[RopeTeam] Follower emptied {count} carried pitons into the rack"
            : $"[RopeTeam] Follower is carrying {count} pitons back");
    }

    internal static void AcceptPitonPool(int fromPlayerId, byte[] payload)
    {
        if (!PitonPool.TryRead(payload, out var linkId, out var count, out var stored)) return;
        if (stored)
        {
            if (!TeamAnchorSession.IsLocalFollower || linkId != _pitonPoolId) return;
            var removed = RemoveCarriedPitons(count);
            var kept = PitonPool.KeptByFollower(CountCarriedPitons() + removed, removed);
            ModLog.Info(kept == 0
                ? $"[RopeTeam] Follower handed {removed} pitons to the leader"
                : $"[RopeTeam] Follower handed {removed} pitons to the leader and kept {kept}");
            return;
        }

        if (PitonPool.ShouldHold(TeamAnchorSession.IsLocalLeader))
        {
            _heldPool = true;
            _heldPoolFrom = fromPlayerId;
            _heldPoolLink = linkId;
            _heldPoolCount = count;
            return;
        }

        GrantPool(fromPlayerId, linkId, count);
    }

    private static void TakeHeldPool()
    {
        if (!_heldPool) return;
        var from = _heldPoolFrom;
        var linkId = _heldPoolLink;
        var count = _heldPoolCount;
        _heldPool = false;
        GrantPool(from, linkId, count);
    }

    private static void GrantPool(int fromPlayerId, int linkId, int count)
    {
        if (!PitonPool.ShouldApply(
                TeamAnchorSession.IsLocalLeader,
                fromPlayerId,
                TeamAnchorSession.FollowerId,
                linkId,
                _pitonPoolId,
                _appliedPitonPoolId))
            return;
        _appliedPitonPoolId = linkId;

        var manager = InventoryManager.Instance;
        var inventory = manager?.PitonInventoryData;
        var leaderCount = CountPitons(manager, inventory);
        var max = ReadPitonBagLimit(inventory);
        var forming = !_rackFormed;
        var requested = PitonPool.FreshPitonsForLeader(leaderCount, count, max);
        GrantFreshPitons(manager, requested);
        var gained = CountPitons(manager, inventory) - leaderCount;
        var gain = PitonReturn.RecordedGain(requested, gained);
        _rackFormed = true;
        ModLog.Info($"[RopeTeam] Leader bag held {leaderCount}, follower contributed {count}, gained {gain} new pitons");
        if (!forming)
            TellFollowerWhatFit(linkId, gain);
    }

    private static void TellFollowerWhatFit(int linkId, int stored)
    {
        if (_poolNetwork == null || _pitonPoolChannel == 0) return;
        _poolNetwork.SendFeatureStream(
            _pitonPoolChannel, PitonPool.WriteStored(linkId, stored), reliable: true);
    }

    private static int CountCarriedPitons()
    {
        var manager = InventoryManager.Instance;
        return CountPitons(manager, manager?.PitonInventoryData);
    }

    private static int ClearCarriedPitons() => RemoveCarriedPitons(int.MaxValue);

    private static int RemoveCarriedPitons(int count)
    {
        if (count <= 0) return 0;
        try
        {
            var manager = InventoryManager.Instance;
            var inventory = manager?.PitonInventoryData;
            if (manager == null || inventory == null) return 0;

            var before = CountPitons(manager, inventory);
            var target = count >= before ? 0 : before - count;
            for (var index = inventory.Count - 1; index >= 0 && CountPitons(manager, inventory) > target; index--)
                inventory.RemoveAt(index);
            if (target == 0 && CountPitons(manager, inventory) > 0)
                manager.RemoveAllPitons(out _);
            var removed = before - CountPitons(manager, inventory);
            return removed < 0 ? 0 : removed;
        }
        catch (Exception exception)
        {
            ModLog.Warning("[RopeTeam] Could not empty the follower's pitons: " + exception.Message);
            return 0;
        }
    }

    private static int ReadPitonBagLimit(InventoryData inventory)
    {
        if (inventory == null) return 0;
        try { return inventory.MaxItemCount; }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.piton-pool-max", exception);
            return 0;
        }
    }

    private static void GrantFreshPitons(InventoryManager manager, int count)
    {
        if (manager == null || count <= 0) return;
        var itemId = (InventoryItemStringId)(int)InventoryItemStringIdEnum.ITEM_PITON;
        try
        {
            for (var index = 0; index < count; index++)
            {
                var request = StorageRequest(itemId);
                if (!manager.CanPutItemParts(request)) break;
                var response = manager.PutItemParts(request);
                if (!response.CompleteSuccess) break;
            }
        }
        catch (Exception exception)
        {
            ModLog.Warning("[RopeTeam] Could not store the pooled pitons: " + exception.Message);
        }
    }

    private static int CountPitons(InventoryManager manager, InventoryDataBase inventory)
    {
        if (manager == null || inventory == null) return 0;
        try { return manager.GetPitonCount(inventory, null); }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.piton-recovery-count", exception);
            return 0;
        }
    }

    /// <summary>The lifeline has already dropped the piton. A mesh left behind is the unclip ghost.</summary>
    private static void DestroyReleasedPiton(Il2Cpp.Piton piton)
    {
        try
        {
            if (piton != null) UnityEngine.Object.Destroy(piton.gameObject);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.piton-recovery-destroy", exception);
        }
    }

    /// <summary>Where the climber is. The harness is on the body; the pawn root can lag behind it.</summary>
    private static bool TryGetClimberPosition(out System.Numerics.Vector3 position)
    {
        position = default;
        try
        {
            var harness = ResolveLocalHarness();
            if (harness != null && TryGetHarnessAttachPosition(harness, out var harnessPosition)
                && TryUsePosition(harnessPosition, out position))
                return true;

            var pawn = ResolveLocalClimbController();
            var body = pawn?.bodySkinnedMeshRenderer;
            if (body != null && TryUsePosition(body.bounds.center, out position)) return true;
            if (pawn != null && TryUsePosition(pawn.Position, out position)) return true;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.piton-recovery-climber", exception);
        }

        return LocalPlayerInterop.TryGetPose(out var pose, out _) && TryUsePosition(pose, out position);
    }

    /// <summary>The practice follower's harness, then his ghost root, then the pose we store for him.</summary>
    private static bool TryGetPracticeFollowerPosition(NetworkManager network, out System.Numerics.Vector3 position)
    {
        position = default;
        if (network == null || !TeamAnchorSession.IsLocalLeader || !PracticePartner.Is(TeamAnchorSession.FollowerId))
            return false;
        if (RemotePlayerManager.TryGetGhostHarness(PracticePartner.PlayerId, out var harness)
            && TryGetHarnessAttachPosition(harness, out var harnessPosition)
            && TryUsePosition(harnessPosition, out position))
            return true;
        if (RemotePlayerManager.TryGetGhostTransform(PracticePartner.PlayerId, out var ghost)
            && TryUsePosition(ghost.position, out position))
            return true;
        return network.RemotePlayers.TryGetValue(PracticePartner.PlayerId, out var remote)
               && remote != null
               && TryUsePosition(new Vector3(remote.X, remote.Y, remote.Z), out position);
    }

    /// <summary>
    /// Where the team rope passes through this chain piton's quickdraw. That is the rope
    /// carabiner, not the piton in the rock.
    /// </summary>
    internal static bool TryGetQuickdrawCarabiner(uint chainId, out Vector3 position)
    {
        position = default;
        if (chainId == 0) return false;
        try
        {
            var found = TeamAnchorSession.IsLocalLeader
                ? _pitons.TryGetOwnPointer(chainId, out var pointer)
                : _pitons.TryGetRemotePointer(chainId, out pointer);
            if (!found || pointer == IntPtr.Zero || !TryReadCarabiner(new Il2Cpp.Piton(pointer), out var point))
                return false;
            position = new Vector3(point.X, point.Y, point.Z);
            return true;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.quickdraw-carabiner", exception);
            return false;
        }
    }

    private static bool TryReadCarabiner(Il2Cpp.Piton piton, out System.Numerics.Vector3 position)
    {
        position = default;
        var quickdraw = piton.Quickdraw ?? piton.includedQuickdraw;
        var end = quickdraw?.GetAttachEnd();
        var collider = end?.sourceCollider;
        if (collider != null && TryUsePosition(collider.bounds.center, out position)) return true;
        if (TryUsePosition(piton.quickdrawEndRigidBody?.position ?? default, out position)) return true;
        return false;
    }

    /// <summary>The drawn piton. Its root transform can sit on the climber while the mesh stays in the rock.</summary>
    private static bool TryGetPitonPosition(Il2Cpp.Piton piton, out System.Numerics.Vector3 position)
    {
        position = default;
        try
        {
            var mesh = piton.pitonRenderer?.mr;
            if (mesh != null && TryUsePosition(mesh.bounds.center, out position)) return true;
            if (TryUsePosition(piton.quickdrawBeginRigidBody?.position ?? default, out position)) return true;
            if (TryUsePosition(piton.quickdrawEndRigidBody?.position ?? default, out position)) return true;
            if (TryUsePosition(piton.transform?.position ?? default, out position)) return true;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.piton-recovery-scan", exception);
        }

        return false;
    }

    private static bool IsOffBelay(Il2Cpp.Lifeline lifeline, Il2Cpp.Piton piton)
    {
        try
        {
            var offBelay = lifeline.OffBelayPiton;
            return offBelay != null && offBelay.Pointer != IntPtr.Zero && offBelay.Pointer == piton.Pointer;
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.piton-recovery-scan", exception);
            return false;
        }
    }

    private static bool TryUsePosition(Vector3 world, out System.Numerics.Vector3 position)
    {
        position = default;
        if (!float.IsFinite(world.x) || !float.IsFinite(world.y) || !float.IsFinite(world.z) || world == Vector3.zero)
            return false;
        position = new System.Numerics.Vector3(world.x, world.y, world.z);
        return true;
    }

    private static bool LifelineHolds(Il2Cpp.Lifeline lifeline, IntPtr pointer)
    {
        var placed = lifeline?.PlacedPitons;
        if (placed == null) return false;
        for (var i = 0; i < placed.Count; i++)
        {
            var piton = placed[i]?.Piton;
            if (piton != null && piton.Pointer == pointer) return true;
        }

        return false;
    }

    /// <summary>
    /// Fills the frame's list. Positions are read only for pitons not yet accounted for:
    /// they are what tells two pitons apart, and reading them costs a native call each.
    /// </summary>
    private static bool TryCollectPlacedPitons(Il2Cpp.Lifeline lifeline)
    {
        _placedPitons.Clear();
        var placedPitons = lifeline.PlacedPitons;
        if (placedPitons == null) return false;

        for (var i = 0; i < placedPitons.Count; i++)
        {
            var piton = placedPitons[i]?.Piton;
            if (piton == null || piton.Pointer == IntPtr.Zero) continue;

            var position = default(System.Numerics.Vector3);
            if (!_pitons.Tracks(piton.Pointer))
            {
                // The list can briefly contain a destroyed entry during reload/removal.
                try
                {
                    var transform = piton.transform;
                    if (transform == null || transform.Pointer == IntPtr.Zero) continue;
                    var p = transform.position;
                    // A hollow saved piton is not anyone's piton. The session never hears
                    // of it, so its clean-up is neither a take nor a withdrawal.
                    if (SavedPitonLoadGuard.IsHollow(p.x, p.y, p.z)) continue;
                    position = new System.Numerics.Vector3(p.x, p.y, p.z);
                }
                catch (Exception exception)
                {
                    ModLog.SuppressedException("rope.resolve-local-piton", exception);
                    continue;
                }
            }

            _placedPitons.Add(new PlacedPiton(piton.Pointer, position));
        }

        return true;
    }

    private static void AnnounceLocalPiton(NetworkManager network, uint localId, IntPtr pointer)
    {
        try
        {
            var piton = new Il2Cpp.Piton(pointer);
            var t = piton.transform;
            if (t == null || t.Pointer == IntPtr.Zero) return;
            var position = t.position;
            var rotation = t.rotation;
            var quality = (byte)piton.executionQuality;
            var hp = piton.pitonHp;
            var itemId = piton.ItemId.value;

            network.SendPitonPlaced(new ClientPitonPlaced
            {
                PitonId = localId,
                PosX = position.x,
                PosY = position.y,
                PosZ = position.z,
                RotX = rotation.x,
                RotY = rotation.y,
                RotZ = rotation.z,
                RotW = rotation.w,
                Quality = quality,
                PitonHp = hp,
                ItemId = itemId,
            });
            TeamAnchorSession.NoteLocalLeaderPlacement(localId,
                new System.Numerics.Vector3(position.x, position.y, position.z));
            ModLog.Debug($"[Piton] Local piton #{localId} detected @ ({position.x:F1},{position.y:F1},{position.z:F1}) quality={quality} hp={hp} itemId={itemId}");
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.announce-local-piton", exception);
        }
    }

    /// <summary>
    /// Spawns a piton through Cairn's typed Lifeline API. Passing the local climbing
    /// controller lets the game initialize its ClimbingSetting before the piton is
    /// visible to Lifeline.Update or save serialization.
    /// </summary>
    private static void SpawnRemotePiton(Il2Cpp.Lifeline lifeline, ServerPitonPlaced placed, double now)
    {
        var controller = ResolveLocalClimbController();
        if (controller == null || controller.Pointer == IntPtr.Zero)
        {
            ModLog.Warning("[Piton] Local ClimbingV2PawnController not found — cannot create a save-safe remote piton");
            _pitons.SpawnFailed(placed.PitonId, now);
            return;
        }
        if (!TryCollectPitonPointers(lifeline, _pitonPointers))
        {
            _pitons.SpawnFailed(placed.PitonId, now);
            return;
        }

        try
        {
            lifeline.AddPiton(new Vector3(placed.PosX, placed.PosY, placed.PosZ),
                new Quaternion(placed.RotX, placed.RotY, placed.RotZ, placed.RotW),
                (Il2Cpp.PitonExecutionQuality)placed.Quality, placed.PitonHp,
                (Il2Cpp.InventoryItemStringId)placed.ItemId, controller);
        }
        catch (Exception ex)
        {
            ModLog.Error($"[Piton] SpawnRemotePiton failed: {ex.Message}");
        }

        // Native code may allocate and then throw: whatever appeared is accounted for.
        var spawned = IntPtr.Zero;
        if (TryCollectPitonPointers(lifeline, _pitonPointersAfterSpawn))
        {
            foreach (var pointer in _pitonPointersAfterSpawn)
            {
                if (_pitonPointers.Contains(pointer)) continue;
                if (spawned == IntPtr.Zero) spawned = pointer;
                else _pitons.Ignore(pointer);
            }
        }

        if (spawned == IntPtr.Zero)
        {
            _pitons.SpawnFailed(placed.PitonId, now);
            return;
        }

        _pitons.Spawned(placed.PitonId, spawned);
        // AddPiton does not clip a quickdraw once another piton is already on the rope.
        // The stick clips at this moment for that reason. The follower's copy needs the
        // same clip, or the grab has nothing to pull. A higher one is taken off this
        // rope afterwards so a fall still catches on the lower piton.
        if (TeamAnchorSession.IsLocalFollower
            && SecuringRopeIsOnHarness()
            && !PitonIsOnRope(lifeline, spawned))
            TryAttachTeamPiton(spawned);
        ModLog.Debug($"[Piton] Spawned remote piton #{placed.PitonId} via Lifeline.AddPiton @ ({placed.PosX:F1},{placed.PosY:F1},{placed.PosZ:F1}) quality={placed.Quality} hp={placed.PitonHp}");
    }

    private static void DetachWhenAllowed(IntPtr pointer)
    {
        var now = Time.unscaledTime;
        if (_detachRetryAt.TryGetValue(pointer, out var retryAt) && now < retryAt) return;
        // A failed native removal keeps the piton accounted for. Destroying only the visual
        // could leave a dangling entry in the native save/rope collection.
        if (TryDetachPitonViaLifeline(pointer)) _detachRetryAt.Remove(pointer);
        else _detachRetryAt[pointer] = now + DetachRetrySeconds;
    }

    /// <summary>
    /// Another climber took this piton, or the host withdrew it. Unclip it, then destroy
    /// the hardware. The piton is already in their bag, so this climber does not gain one.
    /// </summary>
    private static void StripWhenAllowed(IntPtr pointer)
    {
        var now = Time.unscaledTime;
        if (_detachRetryAt.TryGetValue(pointer, out var retryAt) && now < retryAt) return;

        var lifeline = TryGetLifeline();
        if (lifeline == null)
        {
            _detachRetryAt[pointer] = now + DetachRetrySeconds;
            return;
        }

        bool attached;
        try
        {
            attached = lifeline.AttachedPitonsContains(new Il2Cpp.Piton(pointer));
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.strip-piton", exception);
            _detachRetryAt[pointer] = now + DetachRetrySeconds;
            return;
        }

        if (attached && !TryDetachPitonViaLifeline(pointer))
        {
            _detachRetryAt[pointer] = now + DetachRetrySeconds;
            return;
        }

        _detachRetryAt.Remove(pointer);
        DestroyReleasedPiton(new Il2Cpp.Piton(pointer));
    }

    // Native detach needs the Piton pointer; destroying only its GameObject leaves rope state dangling.
    private static bool TryDetachPitonViaLifeline(IntPtr pitonPtr)
    {
        if (pitonPtr == IntPtr.Zero) return false;

        var lifeline = TryGetLifeline();
        if (lifeline == null) return false;

        try
        {
            lifeline.DetachPiton(new Il2Cpp.Piton(pitonPtr));
            ModLog.Debug("[Piton] Detached a piton via Lifeline.DetachPiton");
            return true;
        }
        catch (Exception ex)
        {
            ModLog.Warning($"[Piton] DetachPiton failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Untie takes every chain piton off the rope. The hardware stays in the rock, so a climber
    /// can still pick it up, and it no longer catches a fall.
    /// </summary>
    private static void ReleaseUnhookedPitons(Il2Cpp.Lifeline lifeline)
    {
        TeamAnchorSession.CopyReleased(_releaseIds);
        if (_releaseIds.Count == 0)
        {
            _releaseAnnounced = false;
            return;
        }

        if (!_releaseAnnounced)
        {
            _releaseAnnounced = true;
            ModLog.Info("[Piton] Untie took the shared pitons off the rope. They stay in the rock.");
        }

        var now = Time.unscaledTime;
        foreach (var piton in _releaseIds)
        {
            var key = (piton.LocalId, piton.Id);
            if (_releaseRetryAt.TryGetValue(key, out var retryAt) && now < retryAt) continue;
            if (!TryResolveReleasedPointer(piton, out var pointer))
            {
                _releaseRetryAt[key] = now + DetachRetrySeconds;
                continue;
            }

            bool attached;
            try
            {
                attached = lifeline.AttachedPitonsContains(new Il2Cpp.Piton(pointer));
            }
            catch (Exception ex)
            {
                ModLog.SuppressedException("rope.release-piton", ex);
                _releaseRetryAt[key] = now + DetachRetrySeconds;
                continue;
            }

            if (!attached || TryDetachPitonViaLifeline(pointer))
            {
                // The detach drops it from the placed list, sometimes not until a later
                // frame. Remember it now, and keep remembering until the list lets go.
                _pitons.KeepUnclipped(pointer);
                if (!_rememberUnclipped.Contains(pointer)) _rememberUnclipped.Add(pointer);
                _releaseRetryAt.Remove(key);
                TeamAnchorSession.MarkReleased(piton);
            }
            else
            {
                _releaseRetryAt[key] = now + DetachRetrySeconds;
            }
        }
    }

    /// <summary>
    /// The untie already unclipped these. Keep each one until a reconcile has seen it
    /// off the placed list. While it is still listed, that reconcile forgets the mark,
    /// so the mark has to be put back afterwards.
    /// </summary>
    private static void RememberUnclippedPitons()
    {
        for (var i = _rememberUnclipped.Count - 1; i >= 0; i--)
        {
            var pointer = _rememberUnclipped[i];
            if (!_pitons.KeepUnclipped(pointer) || !_pitons.WasPlaced(pointer))
                _rememberUnclipped.RemoveAt(i);
        }
    }

    private static bool TryResolveReleasedPointer(TeamAnchorSession.ChainRelease piton, out IntPtr pointer)
        => piton.LocalId
            ? _pitons.TryGetOwnPointer(piton.Id, out pointer)
            : _pitons.TryGetRemotePointer(piton.Id, out pointer);

    /// <summary>
    /// The follower's securing rope stops at their anchor. Newer team pitons get a quickdraw
    /// when they are placed, then leave this rope so they are not the follower's last protection.
    /// The quickdraw stays in the rock. When the anchor comes off, the next piton is clipped on.
    /// </summary>
    private static void ApplyFollowerAnchorRope()
    {
        if (!TeamAnchorSession.IsLocalFollower)
        {
            _teamDetachRetryAt.Clear();
            return;
        }

        TeamAnchorSession.CopyRopeEdits(_teamDetachIds, _teamReattachIds);
        // The climber who placed these pitons still has the local numbers after a practice
        // switch. A real follower's chain is the host's numbers.
        var own = TeamAnchorSession.OwnsLocalChainIds;
        var now = Time.unscaledTime;
        var lifeline = TryGetLifeline();
        // The harness clip lands in LateUpdate, after this pass. Attaching before
        // that leaves the quickdraw off, and the follower then falls unsecured.
        var ropeOnHarness = SecuringRopeIsOnHarness();
        ClipMissingQuickdraws(own, ropeOnHarness, now);
        if (!_unclipClearsQuickdraw)
        {
            foreach (var id in _teamDetachIds)
            {
                if (TeamEditWaiting(id, now) || !TryChainPointer(id, own, out var pointer)) continue;
                var hadQuickdraw = PitonKeepsQuickdraw(pointer);
                if (!TryDetachPitonViaLifeline(pointer))
                {
                    _teamDetachRetryAt[id] = now + DetachRetrySeconds;
                    continue;
                }

                if (hadQuickdraw && !PitonKeepsQuickdraw(pointer))
                {
                    // The unclip took the quickdraw with it. Stop unclipping. The piton
                    // stays on the rope so it can still be taken.
                    _unclipClearsQuickdraw = true;
                    ModLog.Info("[RopeTeam] Taking a higher piton off the rope removed its quickdraw. It stays on the rope so it can be taken.");
                    TryAttachTeamPiton(pointer);
                    break;
                }

                // The unclip can drop the piton from the placed list while the hardware
                // stays in the rock. That is not a take. Without the pointer, the next
                // piton cannot be clipped on, and the grab never sees it.
                _pitons.KeepUnclipped(pointer);

                if (lifeline != null && PitonIsOnRope(lifeline, pointer))
                {
                    NoteOnce(_quickdrawMissLogged, id, "[RopeTeam] A higher piton stayed on the rope. Trying again.");
                    _teamDetachRetryAt[id] = now + DetachRetrySeconds;
                    continue;
                }

                _teamDetachRetryAt.Remove(id);
                _quickdrawMissLogged.Remove(id);
                TeamAnchorSession.MarkDetached(id);
            }
        }

        foreach (var id in _teamReattachIds)
        {
            if (TeamEditWaiting(id, now)) continue;
            if (!TryChainPointer(id, own, out var pointer))
            {
                NoteOnce(_anchorMissLogged, id, "[RopeTeam] The next piton did not clip on. Trying again.");
                _teamDetachRetryAt[id] = now + DetachRetrySeconds;
                continue;
            }

            if (lifeline != null && PitonIsOnRope(lifeline, pointer))
            {
                _teamDetachRetryAt.Remove(id);
                _anchorMissLogged.Remove(id);
                NoteFollowerClip(id);
                continue;
            }

            if (!ropeOnHarness) continue;
            if (TryAttachTeamPiton(pointer))
            {
                _teamDetachRetryAt.Remove(id);
                _anchorMissLogged.Remove(id);
                NoteFollowerClip(id);
            }
            else
            {
                NoteOnce(_anchorMissLogged, id, "[RopeTeam] The next piton did not clip on. Trying again.");
                _teamDetachRetryAt[id] = now + DetachRetrySeconds;
            }
        }
    }

    /// <summary>
    /// A piton the leader placed gets a quickdraw on this copy even when it is above
    /// the anchor. The spawn does this the moment the piton exists. A copy that shows
    /// up a frame late is caught here.
    /// </summary>
    private static void ClipMissingQuickdraws(bool own, bool ropeOnHarness, float now)
    {
        if (!ropeOnHarness) return;
        TeamAnchorSession.CopyIds(_teamChainIds);
        foreach (var id in _teamChainIds)
        {
            // The anchor is clipped below. This pass is the pitons above it.
            if (!_teamDetachIds.Contains(id)) continue;
            if (TeamEditWaiting(id, now)) continue;
            if (!TryChainPointer(id, own, out var pointer))
            {
                NoteOnce(_quickdrawMissLogged, id, "[RopeTeam] A placed piton did not get a quickdraw. Trying again.");
                _teamDetachRetryAt[id] = now + DetachRetrySeconds;
                continue;
            }

            if (PitonKeepsQuickdraw(pointer)) continue;
            if (TryAttachTeamPiton(pointer))
            {
                _teamDetachRetryAt.Remove(id);
                _quickdrawMissLogged.Remove(id);
                continue;
            }

            NoteOnce(_quickdrawMissLogged, id, "[RopeTeam] A placed piton did not get a quickdraw. Trying again.");
            _teamDetachRetryAt[id] = now + DetachRetrySeconds;
        }
    }

    private static bool PitonKeepsQuickdraw(IntPtr pitonPtr)
    {
        if (pitonPtr == IntPtr.Zero) return false;
        try
        {
            var quickdraw = new Il2Cpp.Piton(pitonPtr).Quickdraw;
            return quickdraw != null && quickdraw.Pointer != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            ModLog.SuppressedException("rope.piton-quickdraw", ex);
            return true;
        }
    }

    private static void NoteOnce(HashSet<uint> logged, uint id, string message)
    {
        if (!logged.Add(id)) return;
        ModLog.Info(message);
    }

    private static void NoteFollowerClip(uint id)
    {
        TeamAnchorSession.MarkAttached(id);
        if (TeamAnchorSession.TryGetFollowerAnchor(out var anchor, out _) && anchor == id)
            ModLog.Info("[RopeTeam] The follower's anchor is clipped on.");
    }

    private static bool SecuringRopeIsOnHarness()
    {
        var harness = ResolveLocalHarness();
        var rope = harness != null && harness.PersonalLifeline != null
            ? harness.PersonalLifeline.securingRope
            : null;
        if (rope == null || harness == null) return false;
        try
        {
            return rope.IsAttached(harness.Cast<IRopeHolder>());
        }
        catch (Exception ex)
        {
            ModLog.SuppressedException("rope.follower-harness-clip", ex);
            return false;
        }
    }

    private static bool TeamEditWaiting(uint id, float now)
        => _teamDetachRetryAt.TryGetValue(id, out var retryAt) && now < retryAt;

    private static bool TryAttachTeamPiton(IntPtr pitonPtr)
    {
        var lifeline = TryGetLifeline();
        var controller = ResolveLocalClimbController();
        if (lifeline == null || controller == null || pitonPtr == IntPtr.Zero) return false;
        try
        {
            var piton = new Il2Cpp.Piton(pitonPtr);
            lifeline.AttachToPiton(piton, controller, true);
            return lifeline.AttachedPitonsContains(piton);
        }
        catch (Exception ex)
        {
            ModLog.Warning("[Piton] AttachToPiton failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Cairn plays the fall. With no piton on the chain, the partner's fall starts this one too.
    /// </summary>
    internal static void FallUnsecuredWithPartner()
    {
        var pawn = ResolveLocalClimbController();
        if (pawn == null) return;
        try
        {
            if (pawn.IsFalling || pawn.IsSecureFalling) return;
            StartUnsecuredFall(pawn, FallReason.Unknown);
        }
        catch (Exception ex)
        {
            ModLog.Warning("[RopeTeam] Cairn refused the unsecured fall: " + ex.Message);
        }
    }

    private static Il2Cpp.ClimbingV2PawnController ResolveLocalClimbController()
    {
        if (_localClimbControllerCached != null) return _localClimbControllerCached;
        try
        {
            _localClimbControllerCached = Il2Cpp.PawnManager.Instance?.ClimbingPawnController;
            if (_localClimbControllerCached != null)
                ModLog.Debug("[Piton] Local ClimbingV2PawnController resolved");
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("rope.resolve-climbing-controller", exception);
        }
        return _localClimbControllerCached;
    }

    private static bool TryCollectPitonPointers(Il2Cpp.Lifeline lifeline, HashSet<IntPtr> pointers)
    {
        pointers.Clear();
        var placedPitons = lifeline?.PlacedPitons;
        if (placedPitons == null) return false;

        for (var i = 0; i < placedPitons.Count; i++)
        {
            var piton = placedPitons[i]?.Piton;
            if (piton != null && piton.Pointer != IntPtr.Zero)
                pointers.Add(piton.Pointer);
        }

        return true;
    }
}
