using System;
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;

namespace CairnMultiplayerMod.Features;

internal sealed class VoiceFrame : IPacket
{
    public const int MaxBytes = 400;
    public uint Sequence;
    public uint Burst;
    public byte[] Opus = Array.Empty<byte>();

    public void Serialize(BinaryWriter writer)
    {
        if (Opus == null || Opus.Length == 0 || Opus.Length > MaxBytes)
            throw new InvalidDataException("Invalid voice frame length.");
        writer.Write(Sequence);
        writer.Write(Burst);
        writer.Write((ushort)Opus.Length);
        writer.Write(Opus);
    }

    public void Deserialize(BinaryReader reader)
    {
        Sequence = reader.ReadUInt32();
        Burst = reader.ReadUInt32();
        var length = reader.ReadUInt16();
        if (length == 0 || length > MaxBytes || reader.BaseStream.Length - reader.BaseStream.Position != length)
            throw new InvalidDataException("Invalid voice frame length.");
        Opus = reader.ReadBytes(length);
    }
}

internal sealed class VoiceFeature : MultiplayerFeature
{
    public override string Id => "voice";
    private Stream<VoiceFrame> _frames;

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _frames = feature.Stream<VoiceFrame>("opus-v1", (player, frame) =>
            Game.Voice.Receive(player, frame.Burst, frame.Sequence, frame.Opus));
        feature.EveryFrame(Tick, FeaturePhase.Always);
        feature.OnSessionEnded(Game.Voice.Reset);
        feature.OnSceneReset(Game.Voice.Reset);
        feature.OnPlayerLeft((id, _) => Game.Voice.RemovePlayer(id));
    }

    private void Tick()
    {
        Game.Voice.Tick(IsConnected);
        while (Game.Voice.TryCapture(out var burst, out var sequence, out var opus))
            _frames.Send(new VoiceFrame { Burst = burst, Sequence = sequence, Opus = opus });
    }
}
