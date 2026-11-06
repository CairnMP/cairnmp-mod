namespace CairnMultiplayerMod.GameApi;

/// <summary>Read-only native measurements for an opt-in fall observation.</summary>
internal readonly record struct BelayFallObservationSample(
    bool ContextValid,
    bool ControlActive,
    bool RouteReady,
    bool HasStation,
    bool Braked,
    bool Falling,
    bool Suspended,
    bool RopeTaut,
    bool PersonallyAnchored,
    long Rope,
    long Station,
    long LastPiton,
    uint Revision,
    float Height,
    float RopeLength,
    float Hp,
    float MaxHp,
    uint DamageCalls,
    float RequestedFallDamage,
    float ObservedFallHpLoss,
    bool LengthApplied = false,
    float Slack = 0,
    bool ObservationAvailable = false,
    bool StationRecorded = true,
    float ReleasedRopeLength = float.NaN,
    float FixedPathLength = float.NaN,
    float ReleasedFinalLength = float.NaN,
    string RouteFailure = null);

internal enum BelayFallLengthSource { NativeDepartureEstimate, ManualStationBudget }
