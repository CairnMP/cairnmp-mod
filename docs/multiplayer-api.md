# CairnMP managed extension API

`CairnMultiplayer.Api` lets another MelonLoader mod participate in a CairnMP session
without using Steam, handling peers, or defining CairnMP packets. All commands go through
the authoritative host.

The public API version is `MultiplayerApi.Version == 1`. It is independent from the mod
version and the wire protocol version.

## Register an extension

Register during the other mod's initialization, before hosting or joining a lobby:

```csharp
using CairnMultiplayer.Api;

var extension = MultiplayerApi.RegisterExtension(new ExtensionRegistration(
    "com.example.shared-goals",
    new Version(1, 2, 0))
{
    Requirement = ExtensionRequirement.Required,
    MinimumPeerVersion = new Version(1, 0, 0),
    MaximumPeerVersion = new Version(1, 9, 99),
});
```

Extension ids must be globally unique, lowercase, and 3-64 characters long. Use a reverse
domain-style id. A duplicate registration is rejected immediately.

- `Optional`: players without a mutually compatible version may join; the extension is
  disabled only for those players.
- `Required`: the host rejects a player when the extension is absent or incompatible.

Both peers' compatibility ranges are checked. The host tells every admitted client which
extensions are enabled for each player.

## Authoritative commands

A command is a client intention handled only by the host:

```csharp
var score = extension.RegisterState<int>("score");
var scored = extension.RegisterEvent<int>("scored");

var addScore = extension.RegisterCommand<int>("add-score", context =>
{
    if (context.Request < 1 || context.Request > 10)
    {
        context.Reject("Score increment must be between 1 and 10.");
        return;
    }

    score.TryGet(out var current);
    context.Set(score, current + context.Request);
    context.Broadcast(scored, context.Request);
});

CommandResult result = await addScore.SendAsync(2);
if (!result.Committed)
    Log(result.Reason);
```

The sender never selects another client. CairnMP sends the request to the host, invokes the
registered handler on the game thread, and routes committed effects only to compatible peers.

The host may also originate an atomic update directly:

```csharp
if (MultiplayerApi.IsHost)
    extension.Commit(context => context.Set(score, 0));
```

## Replicated state

Replicated state is retained by the host and replayed automatically to late joiners:

```csharp
score.Changed += change =>
{
    if (!change.Removed)
        Log($"Score revision {change.Revision}: {change.Value}");
};
```

`context.Set(state, value)` writes global state. `SetForPlayer` writes state scoped to one
CairnMP player id. Player-scoped state is removed automatically when that player leaves.

Only an authoritative command handler may mutate state. Clients can read its latest local
copy through `TryGet` or `TryGetForPlayer`.

## Transient events

Events are not retained and are appropriate for one-off effects such as an animation or
notification:

```csharp
scored.Received += message =>
    Log($"Player {message.SourcePlayerId} scored {message.Payload}");
```

Use replicated state whenever a player joining later needs to know the current value.

## Transaction and abort guarantees

State changes and events requested through `HostCommandContext` are staged. CairnMP publishes
nothing until the handler returns successfully. Calling `Reject`, throwing an exception, or
failing payload validation discards all staged managed effects.

An arbitrary Unity or CairnAPI mutation cannot be rolled back generically. Schedule it with
`context.AfterCommit` so it runs only after managed validation and commit:

```csharp
context.AfterCommit(() => CairnAPI.Banner.Show("Goal complete!", 3f));
```

An exception in this post-commit action is isolated and logged. It cannot undo already committed
state. After three consecutive failures, CairnMP disables only the faulty extension for the
remainder of the session.

## Serialization

Payloads use `PayloadCodec<T>.Json` by default. DTOs should have stable public properties. A mod
may provide a custom codec when it needs a compact format or explicit schema migration:

```csharp
var codec = new PayloadCodec<MyPayload>(Serialize, Deserialize);
var command = extension.RegisterCommand("custom", Handle, codec);
```

The managed payload limit is 48 KiB. CairnMP rejects larger payloads before dispatch.

## Session and players

The static facade exposes:

- `MultiplayerApi.IsConnected` and `IsHost`;
- `LocalPlayer` and `Players`;
- `SessionReady` and `SessionEnded`;
- `PlayerJoined` and `PlayerLeft`;
- `ExtensionDisabled`.

Use `extension.IsEnabledForPlayer(playerId)` before presenting a feature that requires another
player to run the same optional extension.

## Safety defaults

- All extension traffic is reliable and host-authoritative.
- A command times out locally after 10 seconds without a host response.
- A player may issue at most 120 calls to the same extension command per 10 seconds.
- Unknown commands and disabled extensions are rejected without terminating the session.
- Exceptions are isolated from CairnMP and other extensions.
- Raw Steam ids, peers, packet writers, and transport objects are never public API.
