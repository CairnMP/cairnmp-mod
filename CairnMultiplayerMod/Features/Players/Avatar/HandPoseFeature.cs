using System.IO;
using CairnMultiplayer.Shared;
using UnityEngine;

namespace CairnMultiplayerMod.Features.Players.Avatar;

/// <summary>A climber's finger pose, compressed (smallest-three per bone).</summary>
internal sealed class HandPose : IPacket
{
    public byte[] Packed;

    public void Serialize(BinaryWriter writer) => PacketCodec.WriteBytes(writer, Packed);
    public void Deserialize(BinaryReader reader) => Packed = PacketCodec.ReadBytes(reader);
}

/// <summary>
/// Finger poses on the ghosts — what makes a remote climber grip holds instead of showing
/// flat hands.
///
/// Streamed rather than host state: it is polled ~12 times a second and only sent when it
/// changes, and the next capture supersedes the last. Sent reliably though, because a drop
/// on a change-only stream would leave a ghost's fingers frozen on the previous pose until
/// the player moves them again.
/// </summary>
internal sealed class HandPoseFeature : MultiplayerFeature
{
    public override string Id => "handpose";

    private Stream<HandPose> _pose;
    private float _pollTimer;
    private byte[] _lastSent;

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _pose = feature.Stream<HandPose>("pose", ApplyRemote, reliable: true);

        feature.EveryFrame(Tick, FeaturePhase.Gameplay);
        feature.OnSceneReset(() => { _pollTimer = 0f; _lastSent = null; FingerApi.ResetCaches(); });
    }

    private void Tick()
    {
        if (Mod.Instance.LocalState != PlayerState.InGame) return;

        _pollTimer += Time.unscaledDeltaTime;
        if (_pollTimer < Protocol.HandPosePollIntervalSeconds) return;
        _pollTimer = 0f;

        if (!FingerApi.TryCaptureLocalPose(out var packed)) return;
        if (BytesEqual(_lastSent, packed)) return;

        _lastSent = packed;
        _pose.Send(new HandPose { Packed = packed });
    }

    private void ApplyRemote(int fromPlayerId, HandPose pose)
    {
        if (pose.Packed == null || pose.Packed.Length != Protocol.HandPosePackedSize) return;
        if (!Mod.Instance.Network.RemotePlayers.TryGetValue(fromPlayerId, out var player) || player == null) return;

        player.HandPosePacked = pose.Packed;
        player.HasHandPose = true;
    }

    private static bool BytesEqual(byte[] left, byte[] right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null || left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++)
            if (left[i] != right[i]) return false;
        return true;
    }
}
