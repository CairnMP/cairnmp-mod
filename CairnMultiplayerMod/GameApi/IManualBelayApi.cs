using System;

namespace CairnMultiplayerMod.GameApi;

/// <summary>Opt-in remote control of the local climber's existing protection rope.</summary>
internal interface IManualBelayApi
{
    bool Enabled { get; }
    bool CanBeginStation(out string reason) { reason = "Station unavailable."; return false; }
    bool CanClimb(out string reason) { reason = "Protection unavailable."; return false; }
    bool BeginStation(out string reason);
    bool StationValid { get; }
    void EndStation();
    bool BeginClimber(Guid session, out string reason);
    bool SetBrake(Guid session, uint sequence, bool braked);
    ManualBelayStatus Status { get; }
    void Stop(Guid session);
    void Tick();
    void ResetScene();
    void Shutdown();
    bool IsLocalExercise => false;
    bool ToggleLocalExercise(out string reason) { reason = "Local exercise unavailable."; return false; }
    void ToggleLocalBrake() { }
    void SetLocalRopeMotion(int motion) { }
    LocalBelayRopeStatus? LocalRopeStatus => null;
    string LocalExerciseNotice => null;
    SharedBelayRoutePreview? LocalRoutePreview => null;
    bool TryPreviewRoute(WorldPosition station, out SharedBelayRoutePreview preview) { preview = default; return false; }
}

internal readonly record struct LocalBelayRopeStatus(float Capacity, float Released, float Distance,
    float Slack, float Reserve, float RestLength, float ParticleLength, int Motion, bool Braked);

internal readonly record struct SharedBelayRoutePreview(float FixedLength, float AvailableToClimber,
    float ClimberDistance, int ProtectionCount, bool ReachesClimber);

internal readonly struct ManualBelayStatus
{
    internal ManualBelayStatus(bool active, bool suspended, bool requestedBrake, uint requestedSequence,
        uint appliedSequence, float limit, float realDistance, int protectionCount = 0, uint topologyRevision = 0, bool rebinding = false)
    {
        Active = active; Suspended = suspended; RequestedBrake = requestedBrake;
        RequestedSequence = requestedSequence; AppliedSequence = appliedSequence;
        Limit = limit; RealDistance = realDistance;
        ProtectionCount = protectionCount; TopologyRevision = topologyRevision; Rebinding = rebinding;
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
}

internal sealed class UnavailableManualBelayApi : IManualBelayApi
{
    internal static readonly UnavailableManualBelayApi Instance = new();
    public bool Enabled => false;
    public bool StationValid => false;
    public ManualBelayStatus Status => default;
    public bool BeginStation(out string reason) { reason = "Manual belay is unavailable."; return false; }
    public bool BeginClimber(Guid session, out string reason) { reason = "Manual belay is unavailable."; return false; }
    public bool SetBrake(Guid session, uint sequence, bool braked) => false;
    public void EndStation() { }
    public void Stop(Guid session) { }
    public void Tick() { }
    public void ResetScene() { }
    public void Shutdown() { }
}
