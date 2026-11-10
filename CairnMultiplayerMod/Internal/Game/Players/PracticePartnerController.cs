using System;
using CairnMultiplayerMod.Internal.Diagnostics;
using CairnMultiplayerMod.Internal.Game.Roping;
using CairnMultiplayerMod.Internal.Networking;
using Il2Cpp;
using Il2CppTheGameBakers.Cairn.Netplay;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CairnMultiplayerMod.Internal.Game.Players;

/// <summary>
/// Off unless Debug.PracticeClimber is true in MelonPreferences.
/// F9 spawns or removes the practice climber. H trades places with him.
/// The body you leave stays there, and you climb from his spot with the normal controls.
/// </summary>
internal static class PracticePartnerController
{
    internal const string SpawnKeyName = "F9";
    internal const string MoveKeyName = "H";
    internal const string SwapMessageId = "practice.swap";

    private const float RefuseSeconds = 2f;

    private static readonly Key SpawnKey = Key.F9;
    private static readonly Key MoveKey = Key.H;
    private static bool _spawned;
    private static bool _swapped;
    private static bool _partnerClimbing;
    private static float _refuseUntil;
    private static ClimbingV2PawnController.ClimbingModeSetup _parkedClimb;
    private static HudAdapter _hud;

    /// <summary>Debug.PracticeClimber. Off by default, so players never get F9 or H.</summary>
    internal static bool Enabled => ModConfig.PracticeClimber?.Value == true;

    internal static void Bind(HudAdapter hud) => _hud = hud;

    internal static void Tick(NetworkManager network, RuntimeState state)
    {
        if (network == null || state == null) return;
        if (!Enabled || !network.CanHostPracticePartner || SceneRoles.IsMainMenuArea(state.CurrentScene))
        {
            if (_spawned) Despawn(network);
            else HideStatus();
            return;
        }

        if (!InputCaptureState.IsKeyboardCaptured && !MultiplayerPausePatch.IsPauseMenuActive)
            HandleKeys(network, state);
        if (_spawned) MaintainPose(network, state);
        else HideStatus();
    }

    private static void HandleKeys(NetworkManager network, RuntimeState state)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        if (keyboard[SpawnKey].wasPressedThisFrame)
        {
            if (_spawned) Despawn(network);
            else Spawn(network, state);
            return;
        }

        if (!_spawned || !keyboard[MoveKey].wasPressedThisFrame) return;
        SwitchPlaces(network, state);
    }

    private static void Spawn(NetworkManager network, RuntimeState state)
    {
        if (!LocalPlayerInterop.TryGetPose(out var position, out var yaw)
            || !PracticePartnerMotion.TryBeside(
                position.x, position.y, position.z, yaw, PracticePartnerMotion.SpawnSideMeters,
                out var x, out var y, out var z))
        {
            ModLog.Info("[Practice] Could not read where you are standing.");
            return;
        }

        if (!network.TrySpawnPracticePartner(x, y, z, yaw + 180f, state.CurrentScene))
        {
            ModLog.Info("[Practice] His id is already in this lobby, so he was not spawned.");
            return;
        }

        _spawned = true;
        _swapped = false;
        _partnerClimbing = false;
        ModLog.Info("[Practice] Spawned beside you. H switches places. Tie with L or the right stick.");
    }

    private static void Despawn(NetworkManager network)
    {
        _spawned = false;
        _swapped = false;
        _partnerClimbing = false;
        _refuseUntil = 0f;
        _parkedClimb = null;
        HideStatus();
        if (RopeLinkState.IsLinked(network.LocalPlayerId, PracticePartner.PlayerId))
            network.SendRopeClip(PracticePartner.PlayerId, false);
        network.DespawnPracticePartner();
        ModLog.Info("[Practice] Removed.");
    }

    private static void SwitchPlaces(NetworkManager network, RuntimeState state)
    {
        if (!network.RemotePlayers.TryGetValue(PracticePartner.PlayerId, out var remote) || remote == null)
        {
            _spawned = false;
            HideStatus();
            return;
        }

        if (!LocalPlayerInterop.TryGetPose(out var position, out var yaw))
        {
            ModLog.Info("[Practice] Could not read where you are standing.");
            return;
        }

        ReadBody(out var falling, out var dead, out var walkingOrClimbing, out var climbing);
        var block = PracticeSwap.Refusal(
            paused: false, falling, dead, walkingOrClimbing);
        if (block != PracticeSwap.Block.None)
        {
            ModLog.Info("[Practice] Did not switch: " + block);
            return;
        }

        // The shape is taken before the move. A failed move throws it away, so he stays put.
        var captured = PawnCaptureInterop.TryCaptureLocalPlayerFrame(out var frame);
        var you = new PracticeSpot(position.x, position.y, position.z, yaw, climbing);
        var them = new PracticeSpot(remote.X, remote.Y, remote.Z, remote.YawDeg, _partnerClimbing);
        var tied = RopeLinkState.IsLinked(network.LocalPlayerId, PracticePartner.PlayerId);
        var exchange = PracticeSwap.Exchange(you, them, tied, TeamAnchorSession.IsLocalLeader);
        // His wall keeps the holds he had. This body keeps the holds you have now.
        var entering = _parkedClimb;
        var leaving = CaptureClimb();
        // The move puts this body on his spot before he takes yours. Measuring then
        // runs out along the pitons and back, and Cairn is told the rope is spent.
        RopeLengthPatch.HoldLengthAnswers();
        try
        {
            if (!TryPlaceLocal(exchange.You, climbing, entering, leaving))
            {
                _refuseUntil = Time.unscaledTime + RefuseSeconds;
                ModLog.Info("[Practice] Could not switch places.");
                return;
            }

            _parkedClimb = leaving;

            if (!network.TryMovePracticePartner(
                    exchange.Frozen.X, exchange.Frozen.Y, exchange.Frozen.Z, exchange.Frozen.Yaw, state.CurrentScene))
            {
                ModLog.Info("[Practice] Switched, but his body did not take the old spot.");
            }

            network.TryHoldPracticeShape(captured ? frame : default);
            _partnerClimbing = exchange.Frozen.Climbing;
            if (tied && !TeamAnchorSession.ExchangePracticeRoles(PracticePartner.PlayerId))
                ModLog.Info("[Practice] The spots switched, but the rope job did not.");
            _swapped = !_swapped;
            _refuseUntil = 0f;
            ModLog.Info(_swapped
                ? "[Practice] In his body. H switches back."
                : "[Practice] Back in your body. He stays where you left him.");
        }
        finally
        {
            RopeLengthPatch.ReleaseLengthAnswers();
        }
    }

    private static void ReadBody(out bool falling, out bool dead, out bool walkingOrClimbing, out bool climbing)
    {
        falling = false;
        dead = false;
        walkingOrClimbing = false;
        climbing = false;
        try
        {
            var state = PawnCaptureInterop.GetLocalPawnState();
            dead = state == NetFrame.PawnStateType.Dead;
            falling = state == NetFrame.PawnStateType.Falling;
            var pawns = PawnManager.Instance;
            var mode = pawns != null ? pawns.CurrentMode : PawnControllerSwitcher.Mode.None;
            if (mode == PawnControllerSwitcher.Mode.Climbing)
            {
                climbing = true;
                var climb = pawns.ClimbingPawnController;
                if (climb != null && (climb.IsFalling || climb.IsSecureFalling))
                    falling = true;
            }

            // Hovering is the safe stop on a wall spot. H has to work from there.
            walkingOrClimbing = mode == PawnControllerSwitcher.Mode.Walking
                || mode == PawnControllerSwitcher.Mode.Hovering
                || (mode == PawnControllerSwitcher.Mode.Climbing && !falling);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("practice.read-body", exception);
        }
    }

    /// <summary>
    /// Cairn's own move. Forcing a walk while the grip is on the wall leaves the body
    /// there, and the walk then falls. A standing spot uses the climb exit and seats
    /// the walking capsule. A wall spot with nothing under it hovers. The transform
    /// is not written: that leaves the hands on the old rock.
    /// </summary>
    private static ClimbingV2PawnController.ClimbingModeSetup CaptureClimb()
    {
        try
        {
            var pawns = PawnManager.Instance;
            if (pawns == null || pawns.CurrentMode != PawnControllerSwitcher.Mode.Climbing) return null;
            return pawns.ClimbingPawnController?.GenerateCurrentClimbModeSetup();
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("practice.capture-climb", exception);
            return null;
        }
    }

    private static bool TryPlaceLocal(
        PracticeSpot spot,
        bool currentlyClimbing,
        ClimbingV2PawnController.ClimbingModeSetup restore,
        ClimbingV2PawnController.ClimbingModeSetup leaving)
    {
        try
        {
            var pawns = PawnManager.Instance;
            var switcher = pawns?.PawnControllerSwitcher;
            if (switcher == null) return false;
            if (PracticeSwap.UsesStoredClimb(spot.Climbing, restore != null))
                return TryRestoreClimb(pawns, switcher, spot, restore, leaving);

            var position = new Vector3(spot.X, spot.Y, spot.Z);
            var rotation = Quaternion.Euler(0f, spot.Yaw, 0f);
            var place = PracticeSwap.ChoosePlace(
                spot.Climbing, currentlyClimbing, SpotNeedsHover(switcher, position));
            var mode = place.Arrival switch
            {
                PracticeSwap.Arrival.Walk => PawnControllerSwitcher.Mode.Walking,
                PracticeSwap.Arrival.Climb => PawnControllerSwitcher.Mode.Climbing,
                _ => PawnControllerSwitcher.Mode.Hovering,
            };
            var context = place.LeaveClimb
                ? PawnControllerSwitcher.Context.TransitionFromClimb
                : PawnControllerSwitcher.Context.TransitionFromPawnControllerSwitcher;
            var walker = pawns.WalkingPawnController;
            // The climb exit reads this. It is what puts the capsule on the ground.
            if (place.SeatWalkingBody && walker != null)
                walker.SetSecureTeleportInfo(position, rotation * Vector3.forward);

            switcher.TransitionToMode(
                mode,
                new Il2CppSystem.Nullable<Vector3>(position),
                new Il2CppSystem.Nullable<Quaternion>(rotation),
                true,
                context,
                true);

            if (place.SeatWalkingBody && walker != null
                && switcher.CurrentMode == PawnControllerSwitcher.Mode.Walking)
            {
                walker.SetSecureTeleportInfo(position, rotation * Vector3.forward);
                walker.TryTeleportAndMoveCapsuleFromSecurePosition();
            }

            if (switcher.CurrentMode == mode) return true;
            ModLog.Info("[Practice] Could not switch places. The move ended in " + switcher.CurrentMode + ".");
            return false;
        }
        catch (Exception exception)
        {
            ModLog.Warning("[Practice] Could not switch places: " + exception.Message);
            return false;
        }
    }

    /// <summary>
    /// Puts the climb back on the holds that body left with. A fresh climb at the
    /// same point does not have those holds, and the walk that follows it falls.
    /// </summary>
    private static bool TryRestoreClimb(
        PawnManager pawns,
        PawnControllerSwitcher switcher,
        PracticeSpot spot,
        ClimbingV2PawnController.ClimbingModeSetup restore,
        ClimbingV2PawnController.ClimbingModeSetup leaving)
    {
        try
        {
            var position = new Vector3(spot.X, spot.Y, spot.Z);
            var rotation = Quaternion.Euler(0f, spot.Yaw, 0f);
            switcher.TransitionToMode(
                PawnControllerSwitcher.Mode.Climbing,
                new Il2CppSystem.Nullable<Vector3>(position),
                new Il2CppSystem.Nullable<Quaternion>(rotation),
                true,
                PawnControllerSwitcher.Context.TransitionFromPawnControllerSwitcher,
                true);
            if (switcher.CurrentMode != PawnControllerSwitcher.Mode.Climbing)
            {
                switcher.TransitionToMode(
                    PawnControllerSwitcher.Mode.Climbing,
                    new Il2CppSystem.Nullable<Vector3>(position),
                    new Il2CppSystem.Nullable<Quaternion>(rotation),
                    true,
                    PawnControllerSwitcher.Context.TransitionFromPawnControllerSwitcher,
                    false);
            }

            var climb = pawns.ClimbingPawnController;
            if (climb == null) return false;
            climb.ApplyClimbModeSetup(restore);
            if (switcher.CurrentMode == PawnControllerSwitcher.Mode.Climbing) return true;
        }
        catch (Exception exception)
        {
            ModLog.Warning("[Practice] Could not put the holds back: " + exception.Message);
        }

        try
        {
            if (leaving != null)
                pawns.ClimbingPawnController?.ApplyClimbModeSetup(leaving);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("practice.restore-climb", exception);
        }

        return false;
    }

    /// <summary>True when that point has nothing to stand or grip on. A failed check hovers.</summary>
    private static bool SpotNeedsHover(PawnControllerSwitcher switcher, Vector3 position)
    {
        try
        {
            return switcher.ShouldHoverAtPos(position);
        }
        catch (Exception exception)
        {
            ModLog.SuppressedException("practice.hover-check", exception);
            return true;
        }
    }

    private static void MaintainPose(NetworkManager network, RuntimeState state)
    {
        if (!network.RemotePlayers.TryGetValue(PracticePartner.PlayerId, out var remote) || remote == null)
        {
            _spawned = false;
            _swapped = false;
            HideStatus();
            return;
        }

        network.TryMovePracticePartner(remote.X, remote.Y, remote.Z, remote.YawDeg, state.CurrentScene);
        var text = PracticeSwap.StatusText(
            _swapped,
            RopeLinkState.IsLinked(network.LocalPlayerId, PracticePartner.PlayerId),
            TeamAnchorSession.IsLocalLeader);
        if (text != null)
        {
            _hud?.ShowMessage(SwapMessageId, text, 0.35f);
            return;
        }

        if (Time.unscaledTime < _refuseUntil)
        {
            _hud?.ShowMessage(SwapMessageId, PracticeSwap.FailedText, 0.35f);
            return;
        }

        HideStatus();
    }

    private static void HideStatus() => _hud?.HideMessage(SwapMessageId);
}
