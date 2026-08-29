using System.IO;
using CairnMultiplayer.Shared;
using UnityEngine;

namespace CairnMultiplayerMod.Features.Players.Avatar;

/// <summary>
/// How a climber looks to the others: head lamp mode, walking-stick anchor and outfit,
/// packed into one int. They travel together because they change together and are read
/// together when dressing a ghost.
/// </summary>
internal sealed class AppearanceState : IPacket
{
    /// <summary>bits 0-7 lamp mode, 8-15 stick anchor, 16-24 outfit bits.</summary>
    public int Packed;

    public void Serialize(BinaryWriter writer) => writer.Write(Packed);
    public void Deserialize(BinaryReader reader) => Packed = reader.ReadInt32();
}

/// <summary>Cosmetic flags (bit 0 = glowing gloves).</summary>
internal sealed class CosmeticState : IPacket
{
    public byte Flags;

    public void Serialize(BinaryWriter writer) => writer.Write(Flags);
    public void Deserialize(BinaryReader reader) => Flags = reader.ReadByte();
}

/// <summary>
/// Keeps every player's appearance in sync: lamp, stick, outfit and glowing gloves.
///
/// Both are per-player host state, so a player joining mid-session sees everyone dressed
/// correctly straight away — before, they saw default ghosts until each climber happened to
/// change something.
///
/// The values are still handed to RemotePlayer, which is where RemotePlayerManager reads
/// them when it dresses a ghost. Untangling that is a separate job from this migration.
/// </summary>
internal sealed class AppearanceFeature : MultiplayerFeature
{
    public override string Id => "appearance";

    private PerPlayerState<AppearanceState> _appearance;
    private PerPlayerState<CosmeticState> _cosmetics;
    private HostCommand<AppearanceState> _reportAppearance;
    private HostCommand<CosmeticState> _reportCosmetics;

    private float _appearancePollTimer;
    private float _cosmeticPollTimer;
    private int _lastSentAppearance;
    private bool _hasSentAppearance;
    private byte _lastSentCosmetics;
    private bool _hasSentCosmetics;

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _appearance = feature.PerPlayerState<AppearanceState>("look", ApplyAppearance);
        _cosmetics = feature.PerPlayerState<CosmeticState>("cosmetics", ApplyCosmetics);

        _reportAppearance = feature.HostCommand<AppearanceState>("report-look",
            request => request.Publish(_appearance, request.Message));
        _reportCosmetics = feature.HostCommand<CosmeticState>("report-cosmetics",
            request => request.Publish(_cosmetics, request.Message));

        feature.EveryFrame(TickAppearance, FeaturePhase.Gameplay);
        feature.EveryFrame(TickCosmetics, FeaturePhase.Gameplay);

        feature.OnSceneReset(ResetLocalTracking);
        feature.OnSessionEnded(ResetLocalTracking);
    }

    /// <summary>Polls the local look and reports it only when it actually changes.</summary>
    private void TickAppearance()
    {
        if (Mod.Instance.LocalState != PlayerState.InGame) return;

        _appearancePollTimer += Time.unscaledDeltaTime;
        if (_appearancePollTimer < Protocol.LampStatePollIntervalSeconds) return;
        _appearancePollTimer = 0f;

        if (!LampApi.TryGetLocalState(out var lightMode)) return;

        CosmeticApi.TryGetLocalStickAnchorMode(out var anchorMode);
        var outfitBits = CosmeticApi.GetLocalOutfitBits();
        var packed = (lightMode & 0xFF)
                     | ((anchorMode & 0xFF) << 8)
                     | ((outfitBits & CosmeticApi.OutfitBitsMask) << 16);

        if (_hasSentAppearance && _lastSentAppearance == packed) return;

        _lastSentAppearance = packed;
        _hasSentAppearance = true;
        _reportAppearance.Send(new AppearanceState { Packed = packed });
    }

    private void TickCosmetics()
    {
        if (Mod.Instance.LocalState != PlayerState.InGame) return;

        _cosmeticPollTimer += Time.unscaledDeltaTime;
        if (_cosmeticPollTimer < Protocol.CosmeticStatePollIntervalSeconds) return;
        _cosmeticPollTimer = 0f;

        if (!CosmeticApi.TryGetLocalCosmetics(out var flags)) return;
        if (_hasSentCosmetics && _lastSentCosmetics == flags) return;

        _lastSentCosmetics = flags;
        _hasSentCosmetics = true;
        _reportCosmetics.Send(new CosmeticState { Flags = flags });
    }

    private void ApplyAppearance(int playerId, AppearanceState state)
    {
        if (playerId == LocalPlayerId) return;
        if (!Mod.Instance.Network.RemotePlayers.TryGetValue(playerId, out var player) || player == null) return;

        player.LampMode = state.Packed;
        player.HasLampState = true;
    }

    private void ApplyCosmetics(int playerId, CosmeticState state)
    {
        if (playerId == LocalPlayerId) return;
        if (!Mod.Instance.Network.RemotePlayers.TryGetValue(playerId, out var player) || player == null) return;

        player.CosmeticFlags = state.Flags;
        player.HasCosmeticState = true;
    }

    /// <summary>Forces the next poll to report, so a reloaded scene re-publishes our look
    /// instead of assuming the last value still holds.</summary>
    private void ResetLocalTracking()
    {
        _appearancePollTimer = 0f;
        _cosmeticPollTimer = 0f;
        _hasSentAppearance = false;
        _hasSentCosmetics = false;
        LampApi.ResetCaches();
    }
}
