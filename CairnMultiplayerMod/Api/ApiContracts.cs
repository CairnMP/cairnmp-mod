using System;

namespace CairnMultiplayer.Api;

/// <summary>Whether peers may join a lobby without this extension.</summary>
public enum ExtensionRequirement
{
    Optional = 0,
    Required = 1,
}

/// <summary>Public outcome of an authoritative command.</summary>
public enum CommandStatus
{
    Committed = 0,
    Rejected = 1,
    Failed = 2,
    TimedOut = 3,
    Unavailable = 4,
}

/// <summary>Stable identity and compatibility policy of a third-party integration.</summary>
public sealed class ExtensionRegistration
{
    public ExtensionRegistration(string id, Version version)
    {
        Id = id;
        Version = version;
    }

    public string Id { get; }
    public Version Version { get; }
    public Version MinimumPeerVersion { get; init; }
    public Version MaximumPeerVersion { get; init; }
    public ExtensionRequirement Requirement { get; init; } = ExtensionRequirement.Optional;
}

/// <summary>Transport-independent player identity exposed to integrations.</summary>
public readonly struct MultiplayerPlayer
{
    public MultiplayerPlayer(int id, string name, bool isLocal, bool isHost)
    {
        Id = id;
        Name = name ?? "";
        IsLocal = isLocal;
        IsHost = isHost;
    }

    public int Id { get; }
    public string Name { get; }
    public bool IsLocal { get; }
    public bool IsHost { get; }
}

/// <summary>Final authoritative outcome of a command request.</summary>
public readonly struct CommandResult
{
    public CommandResult(uint requestId, CommandStatus status, string reason)
    {
        RequestId = requestId;
        Status = status;
        Reason = reason ?? "";
    }

    public uint RequestId { get; }
    public CommandStatus Status { get; }
    public string Reason { get; }
    public bool Committed => Status == CommandStatus.Committed;
}

/// <summary>One authoritative replicated-state update.</summary>
public readonly struct ReplicatedStateChange<T>
{
    public ReplicatedStateChange(int scopePlayerId, ulong revision, bool removed, T value)
    {
        ScopePlayerId = scopePlayerId;
        Revision = revision;
        Removed = removed;
        Value = value;
    }

    /// <summary>Zero for global state; otherwise the owning CairnMP player id.</summary>
    public int ScopePlayerId { get; }
    public ulong Revision { get; }
    public bool Removed { get; }
    public T Value { get; }
}

/// <summary>One transient event committed and emitted by the host.</summary>
public readonly struct MultiplayerEventMessage<T>
{
    public MultiplayerEventMessage(int sourcePlayerId, T payload)
    {
        SourcePlayerId = sourcePlayerId;
        Payload = payload;
    }

    public int SourcePlayerId { get; }
    public T Payload { get; }
}
