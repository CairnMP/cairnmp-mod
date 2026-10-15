using System.Collections.Generic;
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features;

/// <summary>
/// Dying with a partner around no longer ends the run on the spot: the climber goes down and
/// stays there, and a teammate can come and put them back on their feet.
///
/// Cairn already supports every step — it skips the death screen inside a netplay room, ships
/// a "revive" prompt on the ghost prefab, and can stand a climber up where their body lies.
/// This feature only decides when to use them: hold the death screen back while somebody is
/// still standing, count down, and spend a healing item for the rescue.
/// </summary>
internal sealed class ReviveFeature : MultiplayerFeature
{
    /// <summary>How long a downed climber can wait for help before the run ends.</summary>
    private const float DownedSeconds = 120f;

    /// <summary>A rescue is a second chance, not a bivouac: back up on a third of your health.</summary>
    private const float ReviveHealthRatio = 0.3f;

    private const float NoticeSeconds = 4f;

    /// <summary>A camp brings people back for the price of the walk, not of an item — but it
    /// brings them back tired.</summary>
    private const float BivouacHealthRatio = 0.5f;

    private const GameKey BivouacRecallKey = GameKey.R;

    /// <summary>A climber who has just stood up is not walking yet, and the game refuses to
    /// move anyone who is not. Keep trying for a few seconds, then let them walk down.</summary>
    private const float RecallTeleportWindowSeconds = 5f;

    /// <summary>Used only when the game's own netplay tweakables are not loaded yet.</summary>
    private const float FallbackFallenPartnerCost = 1.5f;

    /// <summary>
    /// How far from the body the host still believes a rescuer. The game's own prompt reaches
    /// a few metres; positions arrive late and smoothed, so the margin is generous — it only
    /// has to rule out a rescue from across the mountain.
    /// </summary>
    private const float RescueReachMeters = 12f;

    /// <summary>How long the host keeps a granted rescue to itself, so a second rescuer
    /// arriving in the same moment is turned away instead of spending an item for nothing.</summary>
    private const float RescueSettleSeconds = 5f;

    private HostCommand<DownedChanged> _reportCondition;
    private PerPlayerState<DownedChanged> _conditions;
    private HostCommand<ReviveRequest> _revive;
    private HostEvent<ReviveGranted> _granted;
    private Broadcast<BivouacRecall> _recall;

    private readonly HashSet<int> _downedPlayers = new();
    private readonly HashSet<int> _outPlayers = new();
    private readonly Dictionary<int, float> _rescuesUnderway = new();
    private readonly List<int> _expiredRescues = new();
    private bool _isDown;
    private bool _isOut;
    private bool _sceneResetSinceOut;
    private bool _rescuePending;
    private float _downedUntil;
    private int _lastShownSecondsLeft = -1;
    private WorldPosition _pendingRecall;
    private float _pendingRecallUntil;
    private bool _recallPromptShown;

    public override string Id => "revive";

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        // Held by the host for each climber, so a player who joins after a fall knows who is
        // lying on the face — and the host can referee rescues from what it knows.
        _conditions = feature.PerPlayerState<DownedChanged>("conditions", OnConditionChanged);
        _reportCondition = feature.HostCommand<DownedChanged>("downed",
            request => request.Publish(_conditions, request.Message));
        _revive = feature.HostCommand<ReviveRequest>("request", JudgeRescue);
        _granted = feature.HostEvent<ReviveGranted>("granted", OnRescueGranted);
        _recall = feature.Broadcast<BivouacRecall>("recall", OnRecalled);

        feature.Game.Life.HoldBackDeathScreen(DecideOnDeath, OnLocalWentDown);
        feature.Game.Life.AddRevivePrompt(CanReviveTeammate, RescueTeammate);

        // Always: a downed climber is not "in gameplay" as far as the sync gate is
        // concerned, and their countdown must keep running regardless.
        feature.EveryFrame(Tick, FeaturePhase.Always);

        feature.OnSceneReset(() => { if (_isOut) _sceneResetSinceOut = true; });
        feature.OnPlayerLeft((playerId, _) => ForgetPlayer(playerId));
        feature.OnSessionEnded(Reset);
    }

    /// <summary>
    /// Answered at the instant the game decides the local climber died.
    ///
    /// One rule covers every mode: the death screen waits as long as somebody is still
    /// standing. Alone, or with the whole team down, the run ends exactly as it does in solo —
    /// but the others still hear of it: a teammate counting on a climber who is already at
    /// the death screen waited for nothing.
    /// </summary>
    private bool DecideOnDeath()
    {
        if (SomebodyCanStillHelp()) return true;
        if (IsConnected) MarkOut();
        return false;
    }

    private bool SomebodyCanStillHelp()
    {
        if (!IsConnected) return false;
        if (!ModeOffersSomethingToTheFallen) return false;
        return CountStandingTeammates() > 0;
    }

    /// <summary>
    /// Only a standing climber can give the fallen anything — a rescue on the face, a
    /// call back to camp, or simply a climb worth watching. A mode offering none of it lets
    /// the run end the way it always did.
    /// </summary>
    private bool ModeOffersSomethingToTheFallen
        => Rules.TeammateRevive || Rules.BivouacRevive || Rules.SpectateAfterDeath;

    private int CountStandingTeammates()
    {
        // A teammate at camp is still standing — they are the one who can call the fallen
        // back. Counting only climbers on the wall ended every downed run the moment the last
        // of them sat down by the fire.
        var standing = 0;
        foreach (var playerId in Game.Players.RemotePlayersInGameOrBivouac)
            if (!_downedPlayers.Contains(playerId) && !_outPlayers.Contains(playerId)) standing++;
        return standing;
    }

    private void OnLocalWentDown()
    {
        _isDown = true;
        _isOut = false;
        _downedUntil = Game.Time.UnscaledTime + DownedSeconds;
        _lastShownSecondsLeft = -1;
        Report(ClimberCondition.Down);
        LogInfo($"Down, waiting for help for {DownedSeconds:F0}s");
    }

    private void Tick()
    {
        TickPendingRecall();
        TickRescuesUnderway();

        if (!_isDown)
        {
            TickBackInTheClimb();
            TickDeadWeight();
            TickBivouacRecall();
            return;
        }

        // Without a field rescue there is nothing to count down to: the climber watches — or
        // waits for a camp, in a mode that offers one — for as long as somebody is still
        // climbing. The run ends when nobody is, otherwise a wiped-out team would sit in the
        // spectator seat forever.
        if (!Rules.TeammateRevive)
        {
            if (!Game.Life.IsLocalPlayerDown) { StandBackUp(); return; }
            if (!IsConnected || CountStandingTeammates() == 0) EndRun("nobody left standing");
            return;
        }

        // The game itself is the authority on being alive: standing back up is what
        // ReviveLocalPlayer produces, and nothing else can clear it.
        if (!Game.Life.IsLocalPlayerDown)
        {
            StandBackUp();
            return;
        }

        if (!IsConnected || CountStandingTeammates() == 0)
        {
            EndRun("nobody left standing");
            return;
        }

        var secondsLeft = _downedUntil - Game.Time.UnscaledTime;
        if (secondsLeft <= 0f)
        {
            EndRun("nobody came in time");
            return;
        }

        ShowCountdown(secondsLeft);
    }

    private void ShowCountdown(float secondsLeft)
    {
        var whole = (int)secondsLeft;
        if (whole == _lastShownSecondsLeft) return;
        _lastShownSecondsLeft = whole;
        Game.Hud.ShowMessage("countdown", $"Down — {whole}s left. A teammate can still reach you.", 2f);
    }

    private void StandBackUp()
    {
        ClearDownedState();
        Report(ClimberCondition.Standing);
    }

    private void EndRun(string reason)
    {
        LogInfo($"Ending the run: {reason}");
        ClearDownedState();
        // Not "standing": the death is about to play out, and announcing the climber back on
        // their feet kept their teammates waiting on them.
        MarkOut();
        Game.Life.EndLocalPlayer();
    }

    private void MarkOut()
    {
        _isOut = true;
        _sceneResetSinceOut = false;
        Report(ClimberCondition.Out);
    }

    /// <summary>
    /// A death that played out ends with the game loading the climber back somewhere safe —
    /// a scene boundary, then a living climber in the world. Only then do they count as
    /// standing again.
    /// </summary>
    private void TickBackInTheClimb()
    {
        if (!_isOut || !_sceneResetSinceOut) return;
        if (Game.Life.IsLocalPlayerDown || !Game.State.IsLocalPlayerInGame) return;
        _isOut = false;
        Report(ClimberCondition.Standing);
    }

    private void Report(ClimberCondition condition)
        => _reportCondition.Send(new DownedChanged(condition));

    private void ClearDownedState()
    {
        _isDown = false;
        _downedUntil = 0f;
        _lastShownSecondsLeft = -1;
        Game.Hud.HideMessage("countdown");
    }

    /// <summary>
    /// Drives the game's own prompt on a downed teammate's ghost. The game only offers it
    /// on a body; who is actually waiting for help — rather than already at the death
    /// screen — is the host's record, replayed to latecomers too. What is left to decide is
    /// ours: a rescue costs a healing item, and a downed climber cannot carry anyone.
    /// </summary>
    private bool CanReviveTeammate(int playerId)
    {
        if (!Rules.TeammateRevive) return false;
        if (_isDown || !IsConnected || _rescuePending) return false;
        if (!_downedPlayers.Contains(playerId)) return false;
        return Game.Inventory.HasHealingItem;
    }

    /// <summary>
    /// Asks the host first and pays after: two rescuers reaching the same body together
    /// used to spend an item each for a single rescue.
    /// </summary>
    private void RescueTeammate(int playerId)
    {
        if (!CanReviveTeammate(playerId)) return;

        _rescuePending = true;
        _revive.Send(new ReviveRequest(playerId), (accepted, reason) =>
        {
            _rescuePending = false;
            if (!accepted)
            {
                Game.Hud.ShowMessage("notice", reason, NoticeSeconds);
                return;
            }

            if (!Game.Inventory.TryConsumeHealingItem(out var itemName))
            {
                // Used up between the prompt and the answer. The rescue already went out.
                LogWarning($"Revived player {playerId}, but no healing item was left to spend.");
                return;
            }

            Game.Hud.ShowMessage("notice",
                $"You used your {itemName} on {GetPlayerName(playerId)}.", NoticeSeconds);
            LogInfo($"Revived player {playerId} with '{itemName}'");
        });
    }

    /// <summary>Runs on the host only, which knows every climber's condition.</summary>
    private void JudgeRescue(HostRequest<ReviveRequest> request)
    {
        var target = request.Message.TargetPlayerId;
        var targetName = GetPlayerName(target);
        if (!Rules.TeammateRevive)
        {
            request.Reject("There is no rescue on the face in this mode.");
            return;
        }
        if (target == request.FromPlayerId)
        {
            request.Reject("Nobody can rescue themselves.");
            return;
        }
        if (!IsWaitingForHelp(target))
        {
            request.Reject($"{targetName} is not waiting for help.");
            return;
        }
        if (_rescuesUnderway.ContainsKey(target))
        {
            request.Reject($"Someone is already helping {targetName}.");
            return;
        }
        if (!WithinReach(request.FromPlayerId, target))
        {
            request.Reject($"{targetName} is out of reach.");
            return;
        }

        request.Emit(_granted, new ReviveGranted(target, request.FromPlayerId));
        request.AfterCommit(() => _rescuesUnderway[target] = Game.Time.UnscaledTime + RescueSettleSeconds);
    }

    private bool IsWaitingForHelp(int playerId)
        => playerId == LocalPlayerId ? _isDown : _downedPlayers.Contains(playerId);

    /// <summary>Unknown positions pass: a rescue is never refused for want of a fresh
    /// position, only for one that is plainly too far.</summary>
    private bool WithinReach(int rescuerId, int targetId)
    {
        if (!Game.Players.TryGetLocation(rescuerId, out var rescuer)
            || !Game.Players.TryGetLocation(targetId, out var target))
            return true;

        var dx = rescuer.X - target.X;
        var dy = rescuer.Y - target.Y;
        var dz = rescuer.Z - target.Z;
        return dx * dx + dy * dy + dz * dz <= RescueReachMeters * RescueReachMeters;
    }

    private void TickRescuesUnderway()
    {
        if (_rescuesUnderway.Count == 0) return;
        var now = Game.Time.UnscaledTime;
        _expiredRescues.Clear();
        foreach (var pair in _rescuesUnderway)
            if (now >= pair.Value) _expiredRescues.Add(pair.Key);
        foreach (var playerId in _expiredRescues) _rescuesUnderway.Remove(playerId);
    }

    private void OnRescueGranted(int fromPlayerId, ReviveGranted granted)
    {
        if (!Rules.TeammateRevive) return;
        if (granted.TargetPlayerId != LocalPlayerId || !_isDown) return;
        if (!Game.Life.ReviveLocalPlayer(ReviveHealthRatio))
        {
            LogWarning($"Player {granted.RescuerId} tried to revive us and the game refused.");
            return;
        }

        StandBackUp();
        Game.Hud.ShowMessage("notice",
            $"{GetPlayerName(granted.RescuerId)} got you back on your feet.", NoticeSeconds);
    }

    /// <summary>
    /// A partner hanging from your rope is weight you carry.
    ///
    /// The game already prices this: its netplay tweakables hold a stamina malus per corpse,
    /// meant for exactly this situation. Reading it rather than inventing a number keeps a
    /// rescue expensive in the game's own terms -- and the drain stops short of a critical
    /// state, so carrying someone never kills the carrier by itself.
    /// </summary>
    private void TickDeadWeight()
    {
        if (!IsConnected || _downedPlayers.Count == 0) return;

        var carried = 0;
        foreach (var playerId in _downedPlayers)
            if (Game.Players.IsRopedToLocalPlayer(playerId)) carried++;
        if (carried == 0) return;

        var perSecond = Game.Life.FallenPartnerStaminaCost(FallbackFallenPartnerCost);
        Game.Life.ExhaustLocalPlayer(perSecond * carried * Game.Time.UnscaledDeltaTime);
    }

    /// <summary>
    /// A camp is where a run can be put back together: whoever is still standing calls the
    /// fallen climbers back to the fire. Reaching it is the price, so it costs no item.
    /// </summary>
    private void TickBivouacRecall()
    {
        if (!Rules.BivouacRevive || !IsConnected
            || !Game.State.IsLocalPlayerInBivouac || _downedPlayers.Count == 0)
        {
            if (_recallPromptShown)
            {
                _recallPromptShown = false;
                Game.Hud.HideMessage("recall");
            }
            return;
        }

        if (!_recallPromptShown)
        {
            _recallPromptShown = true;
            Game.Hud.ShowMessage("recall",
                $"{BivouacRecallKey}: call the fallen climbers back to camp", 6f);
        }

        if (KeyboardCaptured || !Game.Input.WasKeyPressed(BivouacRecallKey)) return;
        // Not TryGetLocation: a camper's player location is unknown for the whole camp, so
        // asking for it turned every press into a silent no-op.
        if (!Game.State.TryGetCampPosition(out var camp))
        {
            LogWarning("Could not locate our camp to call the fallen back to it.");
            return;
        }

        var called = _downedPlayers.Count;
        _recall.Send(new BivouacRecall(camp));
        Game.Hud.ShowMessage("notice",
            called == 1 ? "You called a fallen climber back to camp."
                        : $"You called {called} fallen climbers back to camp.", NoticeSeconds);
        LogInfo($"Called {called} downed player(s) back to the bivouac");
    }

    /// <summary>
    /// The recall travels from any player, so the fallen check it before acting on it: it
    /// must come from someone actually sitting at a camp, and point somewhere in the world.
    /// </summary>
    private void OnRecalled(int fromPlayerId, BivouacRecall recall)
    {
        if (!Rules.BivouacRevive || !_isDown) return;
        if (!Game.Players.IsRemotePlayerInBivouac(fromPlayerId))
        {
            LogWarning($"Ignored a call back to camp from player {fromPlayerId}, who is not at one.");
            return;
        }
        var camp = recall.Position;
        if (!PacketValidation.IsValidPose(camp.X, camp.Y, camp.Z, 0f))
        {
            LogWarning($"Ignored a call back to camp from player {fromPlayerId}: invalid position.");
            return;
        }
        if (!Game.Life.ReviveLocalPlayer(BivouacHealthRatio))
        {
            LogWarning($"Player {fromPlayerId} called us back to camp and the game refused.");
            return;
        }

        StandBackUp();
        _pendingRecall = camp;
        _pendingRecallUntil = Game.Time.UnscaledTime + RecallTeleportWindowSeconds;
        Game.Hud.ShowMessage("notice",
            $"{GetPlayerName(fromPlayerId)} brought you back to their camp.", NoticeSeconds);
    }

    private void TickPendingRecall()
    {
        if (_pendingRecallUntil <= 0f) return;
        if (Game.Time.UnscaledTime > _pendingRecallUntil)
        {
            _pendingRecallUntil = 0f;
            LogWarning("Back on our feet, but too far from camp to be moved there.");
            return;
        }

        if (Game.World.TryTeleportLocalPlayer(_pendingRecall, 0f, out _))
            _pendingRecallUntil = 0f;
    }

    private void OnConditionChanged(int playerId, DownedChanged message)
    {
        if (playerId == LocalPlayerId) return;
        _rescuesUnderway.Remove(playerId);

        switch (message.Condition)
        {
            case ClimberCondition.Down:
                _outPlayers.Remove(playerId);
                if (_downedPlayers.Add(playerId))
                    Game.Hud.ShowMessage("notice", DownedNotice(playerId), NoticeSeconds);
                break;
            case ClimberCondition.Out:
                _downedPlayers.Remove(playerId);
                _outPlayers.Add(playerId);
                break;
            default:
                _downedPlayers.Remove(playerId);
                _outPlayers.Remove(playerId);
                break;
        }
    }

    /// <summary>What the others can do about it depends on the mode, so the notice does too.</summary>
    private string DownedNotice(int playerId)
    {
        var name = GetPlayerName(playerId);
        if (Rules.TeammateRevive) return $"{name} is down. Reach them and revive them.";
        if (Rules.BivouacRevive) return $"{name} is down. Make camp to call them back.";
        return $"{name} is out of the climb.";
    }

    private void ForgetPlayer(int playerId)
    {
        _downedPlayers.Remove(playerId);
        _outPlayers.Remove(playerId);
        _rescuesUnderway.Remove(playerId);
    }

    private void Reset()
    {
        // The session was the only thing holding the death screen back. With it gone nobody
        // can come, so a climber still down plays the death out — otherwise they are left in
        // a dead body with no menu to restart from.
        var wasDown = _isDown;
        _downedPlayers.Clear();
        _outPlayers.Clear();
        _rescuesUnderway.Clear();
        _rescuePending = false;
        _isOut = false;
        _sceneResetSinceOut = false;
        _pendingRecallUntil = 0f;
        if (_recallPromptShown)
        {
            _recallPromptShown = false;
            Game.Hud.HideMessage("recall");
        }
        ClearDownedState();
        if (wasDown) Game.Life.EndLocalPlayer();
    }
}

/// <summary>Where a climber stands, as far as their teammates need to know.</summary>
internal enum ClimberCondition : byte
{
    Standing = 0,

    /// <summary>Dead, with the death held back: a rescue or a camp can still bring them in.</summary>
    Down = 1,

    /// <summary>The death played out. They count again once their game has them back on
    /// their feet.</summary>
    Out = 2,
}

internal sealed class DownedChanged : IPacket
{
    public DownedChanged() { }
    public DownedChanged(bool isDown) => Condition = isDown ? ClimberCondition.Down : ClimberCondition.Standing;
    public DownedChanged(ClimberCondition condition) => Condition = condition;

    public ClimberCondition Condition;

    public void Serialize(BinaryWriter writer) => writer.Write((byte)Condition);

    public void Deserialize(BinaryReader reader)
    {
        var raw = reader.ReadByte();
        Condition = raw <= (byte)ClimberCondition.Out ? (ClimberCondition)raw : ClimberCondition.Standing;
    }
}

internal sealed class BivouacRecall : IPacket
{
    public BivouacRecall() { }
    public BivouacRecall(WorldPosition position) => Position = position;

    public WorldPosition Position;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(Position.X);
        writer.Write(Position.Y);
        writer.Write(Position.Z);
    }

    public void Deserialize(BinaryReader reader)
        => Position = new WorldPosition(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}

internal sealed class ReviveRequest : IPacket
{
    public ReviveRequest() { }
    public ReviveRequest(int targetPlayerId) => TargetPlayerId = targetPlayerId;

    public int TargetPlayerId;

    public void Serialize(BinaryWriter writer) => writer.Write(TargetPlayerId);
    public void Deserialize(BinaryReader reader) => TargetPlayerId = reader.ReadInt32();
}

internal sealed class ReviveGranted : IPacket
{
    public ReviveGranted() { }
    public ReviveGranted(int targetPlayerId, int rescuerId)
    {
        TargetPlayerId = targetPlayerId;
        RescuerId = rescuerId;
    }

    public int TargetPlayerId;
    public int RescuerId;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(TargetPlayerId);
        writer.Write(RescuerId);
    }

    public void Deserialize(BinaryReader reader)
    {
        TargetPlayerId = reader.ReadInt32();
        RescuerId = reader.ReadInt32();
    }
}
