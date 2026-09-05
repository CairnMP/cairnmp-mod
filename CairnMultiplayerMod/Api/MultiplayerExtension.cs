using System;
using System.Threading;
using System.Threading.Tasks;
using CairnMultiplayerMod.Internal.Extensions;

namespace CairnMultiplayer.Api;

/// <summary>Registered third-party integration and factory for its managed contracts.</summary>
public sealed class MultiplayerExtension
{
    internal MultiplayerExtension(ExtensionRuntime runtime, ExtensionDefinition definition)
    {
        Runtime = runtime;
        Definition = definition;
    }

    internal ExtensionRuntime Runtime { get; }
    internal ExtensionDefinition Definition { get; }

    public string Id => Definition.Manifest.Id;
    public Version Version => System.Version.Parse(Definition.Manifest.Version);

    public MultiplayerCommand<TRequest> RegisterCommand<TRequest>(
        string commandId,
        Action<HostCommandContext<TRequest>> handler,
        PayloadCodec<TRequest> codec = null)
        => Runtime.RegisterCommand(this, commandId, handler, codec ?? PayloadCodec<TRequest>.Json);

    public ReplicatedState<T> RegisterState<T>(string stateId, PayloadCodec<T> codec = null)
        => Runtime.RegisterState(this, stateId, codec ?? PayloadCodec<T>.Json);

    public MultiplayerEvent<T> RegisterEvent<T>(string eventId, PayloadCodec<T> codec = null)
        => Runtime.RegisterEvent(this, eventId, codec ?? PayloadCodec<T>.Json);

    public bool IsEnabledForPlayer(int playerId) => Runtime.IsExtensionEnabled(Id, playerId);

    /// <summary>Runs a host-originated atomic update without manufacturing a client command.</summary>
    public CommandResult Commit(Action<AuthoritativeContext> buildEffects)
        => Runtime.CommitHostEffects(this, buildEffects);
}

/// <summary>A typed client-to-host command.</summary>
public sealed class MultiplayerCommand<TRequest>
{
    internal MultiplayerCommand(ExtensionRuntime runtime, string extensionId, string commandId, PayloadCodec<TRequest> codec)
    {
        Runtime = runtime;
        ExtensionId = extensionId;
        CommandId = commandId;
        Codec = codec;
    }

    internal ExtensionRuntime Runtime { get; }
    internal string ExtensionId { get; }
    internal string CommandId { get; }
    internal PayloadCodec<TRequest> Codec { get; }

    public Task<CommandResult> SendAsync(TRequest request, CancellationToken cancellationToken = default)
        => Runtime.SendCommandAsync(this, request, cancellationToken);
}
