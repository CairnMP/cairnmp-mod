using System;

namespace CairnMultiplayerMod.GameApi;

/// <summary>Opt-in remote control of the local climber's existing protection rope.</summary>
internal interface IManualBelayApi
{
    bool Enabled { get; }
    bool NeedsTick => false;
    string ControlNotice => null;
    float RopeCapacity => 0;
    bool TryCaptureRopeVisual(Guid session, out WorldPosition[] points) { points = null; return false; }
    void ShowRopeVisual(Guid session, int climber, uint revision, WorldPosition[] points) { }
    void ClearRopeVisual() { }
    bool CanBeginStation(out string reason) { reason = "Station unavailable."; return false; }
    bool CanClimb(out string reason) { reason = "Protection unavailable."; return false; }
    bool TryGetStation(out BelaySharedStation station) { station = default; return false; }
    bool IsStationKnown(BelaySharedStation station) => false;
    bool CanClimb(BelaySharedStation station, out string reason) { reason = "Shared station unavailable."; return false; }
    bool BeginStation(out string reason);
    bool HasStation => false;
    bool StationValid { get; }
    bool ProtectStation(Guid session) => session != Guid.Empty && StationValid;
    void EndStation();
    bool BeginClimber(Guid session, BelaySharedStation station, out string reason)
    { reason = "Shared station unavailable."; return false; }
    bool SetRopeInput(Guid session, uint sequence, bool braked, int motion) => false;
    ManualBelayStatus Status { get; }
    void Stop(Guid session);
    void Tick();
    void ResetScene();
    void Shutdown();
    string InteractionNotice => null;
}

internal readonly record struct BelaySharedStation(Guid Id, WorldPosition Position, int PitonOwner = -1, uint PitonId = 0)
{
    internal bool IsValid => Id != Guid.Empty && PitonOwner >= 0 && PitonId != 0 && float.IsFinite(Position.X)
        && float.IsFinite(Position.Y) && float.IsFinite(Position.Z);
}

internal readonly record struct BelayRopeReadback(int Motion, float Released, float Slack, float Reserve, bool LengthApplied);

internal readonly struct ManualBelayStatus
{
    internal ManualBelayStatus(bool active, bool suspended, bool requestedBrake, uint requestedSequence,
        uint appliedSequence, float limit, float realDistance, int protectionCount = 0, uint topologyRevision = 0, bool rebinding = false,
        BelayRopeReadback? rope = null)
    {
        Active = active; Suspended = suspended; RequestedBrake = requestedBrake;
        RequestedSequence = requestedSequence; AppliedSequence = appliedSequence;
        Limit = limit; RealDistance = realDistance;
        ProtectionCount = protectionCount; TopologyRevision = topologyRevision; Rebinding = rebinding;
        Rope = rope;
    }
    public bool Active { get; }
    public bool Suspended { get; }
    public bool RequestedBrake { get; }
    public uint RequestedSequence { get; }
    public uint AppliedSequence { get; }
    public float Limit { get; }
    public float RealDistance { get; }
    public int ProtectionCount { get; }
    public uint TopologyRevision { get; }
    public bool Rebinding { get; }
    public BelayRopeReadback? Rope { get; }
}

internal sealed class UnavailableManualBelayApi : IManualBelayApi
{
    internal static readonly UnavailableManualBelayApi Instance = new();
    public bool Enabled => false;
    public bool StationValid => false;
    public ManualBelayStatus Status => default;
    public bool BeginStation(out string reason) { reason = "Manual belay is unavailable."; return false; }
    public void EndStation() { }
    public void Stop(Guid session) { }
    public void Tick() { }
    public void ResetScene() { }
    public void Shutdown() { }
}
