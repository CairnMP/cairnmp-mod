using System;

namespace CairnMultiplayerMod.GameApi;

/// <summary>Opt-in remote control of the local climber's existing protection rope.</summary>
internal interface IManualBelayApi
{
    bool Enabled { get; }
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
    bool BeginClimber(Guid session, out string reason);
    bool BeginClimber(Guid session, BelaySharedStation station, out string reason)
    { reason = "Shared station unavailable."; return false; }
    bool SetBrake(Guid session, uint sequence, bool braked);
    bool SetRopeInput(Guid session, uint sequence, bool braked, int motion) => false;
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
    bool ToggleLocalStation(out string reason) { reason = "Local station rehearsal unavailable."; return false; }
    LocalBelayStationStatus? LocalStationStatus => null;
    SharedBelayRoutePreview? LocalRoutePreview => null;
    bool TryPreviewRoute(WorldPosition station, out SharedBelayRoutePreview preview) { preview = default; return false; }
    bool AdvanceLocalStationConnection(out string reason) { reason = "Station connection trial unavailable."; return false; }
    LocalStationConnectionStatus? StationConnectionStatus => null;
    LocalStationConnectionContext StationConnectionContext => default;
}

/// <summary>Solo trial readiness uses the native lifecycle, independent of multiplayer player state.</summary>
internal readonly record struct LocalStationConnectionContext(bool Enabled, bool Offline, bool NativeInGame)
{
    internal bool CanRun => Enabled && Offline && NativeInGame;
    internal bool ShowPanel(bool hasStation, bool hasResult) => Enabled && Offline && (NativeInGame || hasStation || hasResult);
}

internal enum StationConnectionStage { Unclipping, Detached, Connecting, Confirming, Passed, Failed, NotReady }
internal enum StationConnectionFailure
{
    None, Setup, Context, BackupLost, StationLost, RouteChanged, Timeout, NativeRejected,
    StationRequired, BackupRequired, RouteNotReady, NativePartsNotReady, NativePinsMismatch
}
internal readonly record struct LocalStationConnectionStatus(StationConnectionStage Stage, StationConnectionFailure Failure,
    float ConfirmedSeconds, string Detail)
{
    internal bool Active => Stage is StationConnectionStage.Unclipping or StationConnectionStage.Detached
        or StationConnectionStage.Connecting or StationConnectionStage.Confirming;
}

internal readonly record struct BelaySharedStation(Guid Id, WorldPosition Position, int PitonOwner = -1, uint PitonId = 0)
{
    internal bool IsValid => Id != Guid.Empty && PitonOwner >= 0 && PitonId != 0 && float.IsFinite(Position.X)
        && float.IsFinite(Position.Y) && float.IsFinite(Position.Z);
}

internal readonly record struct LocalBelayRopeStatus(float Capacity, float Released, float Distance,
    float Slack, float Reserve, float RestLength, float ParticleLength, int Motion, bool Braked, bool LengthApplied);

internal readonly record struct LocalBelayStationStatus(bool Valid, float FixedLength,
    float AvailableToClimber, int IntermediateProtections, string Notice);

internal readonly record struct SharedBelayRoutePreview(float FixedLength, float AvailableToClimber,
    float ClimberDistance, int ProtectionCount, bool ReachesClimber);

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
    public bool BeginClimber(Guid session, out string reason) { reason = "Manual belay is unavailable."; return false; }
    public bool SetBrake(Guid session, uint sequence, bool braked) => false;
    public void EndStation() { }
    public void Stop(Guid session) { }
    public void Tick() { }
    public void ResetScene() { }
    public void Shutdown() { }
}
