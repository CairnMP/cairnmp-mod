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

    private Broadcast<DownedChanged> _downed;
    private Broadcast<ReviveRequest> _revive;
    private Broadcast<BivouacRecall> _recall;

    private readonly HashSet<int> _downedPlayers = new();
    private bool _isDown;
    private float _downedUntil;
    private int _lastShownSecondsLeft = -1;
    private WorldPosition _pendingRecall;
    private float _pendingRecallUntil;
    private bool _recallPromptShown;

    public override string Id => "revive";

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _downed = feature.Broadcast<DownedChanged>("downed", OnRemoteDownedChanged);
        _revive = feature.Broadcast<ReviveRequest>("request", OnReviveRequested);
        _recall = feature.Broadcast<BivouacRecall>("recall", OnRecalled);

        feature.Game.Life.HoldBackDeathScreen(SomebodyCanStillHelp, OnLocalWentDown);
        feature.Game.Life.AddRevivePrompt(CanReviveTeammate, RescueTeammate);

        // Always: a downed climber is not "in gameplay" as far as the sync gate is
        // concerned, and their countdown must keep running regardless.
        feature.EveryFrame(Tick, FeaturePhase.Always);

        feature.OnPlayerLeft((playerId, _) => _downedPlayers.Remove(playerId));
        feature.OnSessionEnded(Reset);
    }

    /// <summary>
    /// Answered at the instant the game decides the local climber died.
    ///
    /// One rule covers every mode: the death screen waits as long as somebody is still
    /// standing. Alone, or with the whole team down, the run ends exactly as it does in solo.
    /// </summary>
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
        var standing = 0;
        foreach (var playerId in Game.Players.RemotePlayersInGame)
            if (!_downedPlayers.Contains(playerId)) standing++;
        return standing;
    }

    private void OnLocalWentDown()
    {
        _isDown = true;
        _downedUntil = Game.Time.UnscaledTime + DownedSeconds;
        _lastShownSecondsLeft = -1;
        _downed.Send(new DownedChanged(true));
        LogInfo($"Down, waiting for help for {DownedSeconds:F0}s");
    }

    private void Tick()
    {
        TickPendingRecall();

        if (!_isDown)
        {
            TickDeadWeight();
            TickBivouacRecall();
            return;
        }

        // Without a field rescue there is nothing to count down to: the climber waits for a
        // camp, or watches, for as long as somebody is still climbing. The run ends when
        // nobody is — otherwise a wiped-out team would sit in the spectator seat forever.
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
        _downed.Send(new DownedChanged(false));
    }

    private void EndRun(string reason)
    {
        LogInfo($"Ending the run: {reason}");
        ClearDownedState();
        _downed.Send(new DownedChanged(false));
        Game.Life.EndLocalPlayer();
    }

    private void ClearDownedState()
    {
        _isDown = false;
        _downedUntil = 0f;
        _lastShownSecondsLeft = -1;
        Game.Hud.HideMessage("countdown");
    }

    /// <summary>
    /// Drives the game's own prompt on a downed teammate's ghost. Whether they are down is
    /// read from the ghost the game is animating, not from our own bookkeeping: a player who
    /// joined after the fall never received the announcement, and their teammate's body is
    /// lying right there all the same. What is left to decide is ours — a rescue costs a
    /// healing item, and a downed climber cannot carry anyone.
    /// </summary>
    private bool CanReviveTeammate(int playerId)
    {
        if (!Rules.TeammateRevive) return false;
        if (_isDown || !IsConnected) return false;
        return Game.Inventory.HasHealingItem;
    }

    private void RescueTeammate(int playerId)
    {
        if (!CanReviveTeammate(playerId)) return;
        if (!Game.Inventory.TryConsumeHealingItem(out var itemName))
        {
            Game.Hud.ShowMessage("notice", "You have nothing left to treat them with.", NoticeSeconds);
            return;
        }

        _revive.Send(new ReviveRequest(playerId));
        // Sent, not confirmed: the rescued player owns the outcome, and the item is already
        // spent either way. Saying who we helped is enough feedback here.
        Game.Hud.ShowMessage("notice",
            $"You used your {itemName} on {GetPlayerName(playerId)}.", NoticeSeconds);
        LogInfo($"Revived player {playerId} with '{itemName}'");
    }

    private void OnReviveRequested(int fromPlayerId, ReviveRequest request)
    {
        if (!Rules.TeammateRevive) return;
        if (request.TargetPlayerId != LocalPlayerId || !_isDown) return;
        if (!Game.Life.ReviveLocalPlayer(ReviveHealthRatio))
        {
            LogWarning($"Player {fromPlayerId} tried to revive us and the game refused.");
            return;
        }

        StandBackUp();
        Game.Hud.ShowMessage("notice",
            $"{GetPlayerName(fromPlayerId)} got you back on your feet.", NoticeSeconds);
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
    /// A camp is the one place a run can be put back together: whoever is still standing
    /// calls the fallen climbers back to the fire. It is the only way home in a mode that
    /// has no field rescue, which is why it costs nothing but the climb to get here.
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
        if (!Game.Players.TryGetLocation(LocalPlayerId, out var camp)) return;

        var called = _downedPlayers.Count;
        _recall.Send(new BivouacRecall(new WorldPosition(camp.X, camp.Y, camp.Z)));
        Game.Hud.ShowMessage("notice",
            called == 1 ? "You called a fallen climber back to camp."
                        : $"You called {called} fallen climbers back to camp.", NoticeSeconds);
        LogInfo($"Called {called} downed player(s) back to the bivouac");
    }

    private void OnRecalled(int fromPlayerId, BivouacRecall recall)
    {
        if (!Rules.BivouacRevive || !_isDown) return;
        if (!Game.Life.ReviveLocalPlayer(BivouacHealthRatio))
        {
            LogWarning($"Player {fromPlayerId} called us back to camp and the game refused.");
            return;
        }

        StandBackUp();
        _pendingRecall = recall.Position;
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

    private void OnRemoteDownedChanged(int fromPlayerId, DownedChanged message)
    {
        if (message.IsDown)
        {
            if (!_downedPlayers.Add(fromPlayerId)) return;
            Game.Hud.ShowMessage("notice",
                $"{GetPlayerName(fromPlayerId)} is down. Reach them and revive them.", NoticeSeconds);
        }
        else
        {
            _downedPlayers.Remove(fromPlayerId);
        }
    }

    private void Reset()
    {
        _downedPlayers.Clear();
        _pendingRecallUntil = 0f;
        if (_recallPromptShown)
        {
            _recallPromptShown = false;
            Game.Hud.HideMessage("recall");
        }
        ClearDownedState();
    }
}

internal sealed class DownedChanged : IPacket
{
    public DownedChanged() { }
    public DownedChanged(bool isDown) => IsDown = isDown;

    public bool IsDown;

    public void Serialize(BinaryWriter writer) => writer.Write(IsDown);
    public void Deserialize(BinaryReader reader) => IsDown = reader.ReadBoolean();
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
