# CairnMP Managed Extension API

`CairnMultiplayer.Api` lets another MelonLoader mod participate in a CairnMP
session without accessing Steam, managing peers, or defining CairnMP packets.
Every command is handled by the authoritative host.

> [!NOTE]
> The public API version is `MultiplayerApi.Version == 1`. It is independent of
> both the CairnMP release version and the wire-protocol version.

## Contents

- [Register an extension](#register-an-extension)
- [Choose the right primitive](#choose-the-right-primitive)
- [Send authoritative commands](#send-authoritative-commands)
- [Replicate state](#replicate-state)
- [Publish transient events](#publish-transient-events)
- [Understand transactions](#understand-transactions)
- [Serialize payloads](#serialize-payloads)
- [Observe sessions and players](#observe-sessions-and-players)
- [Safety defaults](#safety-defaults)

## Register an extension

Register during your mod’s initialization, before hosting or joining a lobby:

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

Extension IDs must be globally unique, lowercase, and between 3 and 64
characters. Prefer a reverse-domain ID.

| Requirement | Admission behavior |
| --- | --- |
| `Optional` | Incompatible or missing peers may join; the extension is disabled only for them. |
| `Required` | The host rejects a peer when the extension is missing or incompatible. |

Both peers’ compatibility ranges are evaluated. The host tells every admitted
client which extensions are enabled for each player. Duplicate registration is
rejected immediately.

## Choose the right primitive

| Primitive | Use it when… | Retained for late joiners? |
| --- | --- | ---: |
| Command | A client wants the host to validate and commit an intention. | No |
| State | The current value must remain available and be replayed. | Yes |
| Event | Peers need a one-off animation, notification, or effect. | No |

## Send authoritative commands

A command represents client intent and is handled only by the host:

```csharp
var score = extension.RegisterState<int>("score");
var scored = extension.RegisterEvent<int>("scored");

var addScore = extension.RegisterCommand<int>("add-score", context =>
{
    if (context.Request is < 1 or > 10)
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

The sender never chooses another client. CairnMP routes the request to the host,
runs the handler on the game thread, and publishes committed effects only to
compatible peers.

The host can originate an atomic update directly:

```csharp
if (MultiplayerApi.IsHost)
    extension.Commit(context => context.Set(score, 0));
```

## Replicate state

State is retained by the host and replayed automatically to late joiners:

```csharp
score.Changed += change =>
{
    if (!change.Removed)
        Log($"Score revision {change.Revision}: {change.Value}");
};
```

- `context.Set(state, value)` writes global state.
- `context.SetForPlayer(...)` writes state scoped to one CairnMP player ID.
- Player-scoped state is removed automatically when that player leaves.
- Clients read their latest local copy through `TryGet` or `TryGetForPlayer`.

Only an authoritative command handler may mutate state.

## Publish transient events

Events are not retained. Use them for effects that a late joiner does not need to
replay:

```csharp
scored.Received += message =>
    Log($"Player {message.SourcePlayerId} scored {message.Payload}");
```

Use replicated state instead whenever a player joining later must know the
current value.

## Understand transactions

Changes requested through `HostCommandContext` are staged. CairnMP publishes
nothing until the handler returns successfully.

The transaction is discarded when:

- the handler calls `Reject`;
- the handler throws;
- payload validation fails.

Unity or CairnAPI mutations cannot be rolled back generically. Schedule them
after the managed commit:

```csharp
context.AfterCommit(() => CairnAPI.Banner.Show("Goal complete!", 3f));
```

An exception in a post-commit action is isolated and logged, but cannot undo
already committed state. After three consecutive failures, CairnMP disables only
the faulty extension for the remainder of the session.

## Serialize payloads

Payloads use `PayloadCodec<T>.Json` by default. DTOs should expose stable public
properties. Use a custom codec for compact formats or explicit schema migration:

```csharp
var codec = new PayloadCodec<MyPayload>(Serialize, Deserialize);
var command = extension.RegisterCommand("custom", Handle, codec);
```

> [!IMPORTANT]
> Managed payloads are limited to **48 KiB**. CairnMP rejects larger payloads
> before dispatch.

## Observe sessions and players

The static façade exposes:

| Member | Purpose |
| --- | --- |
| `IsConnected`, `IsHost` | Inspect local session role and connectivity. |
| `LocalPlayer`, `Players` | Read the local player and current roster. |
| `SessionReady`, `SessionEnded` | Observe session lifecycle. |
| `PlayerJoined`, `PlayerLeft` | Observe roster changes. |
| `ExtensionDisabled` | React when an extension is isolated. |

Before presenting functionality that depends on an optional extension, call
`extension.IsEnabledForPlayer(playerId)`.

## Safety defaults

- All extension traffic is reliable and host-authoritative.
- Commands time out locally after 10 seconds without a host response.
- A player may issue at most 120 calls to the same extension command per 10
  seconds.
- Unknown commands and disabled extensions are rejected without ending the
  session.
- Exceptions are isolated from CairnMP and from other extensions.
- Raw Steam IDs, peers, packet writers, and transport objects are not exposed.

See the [managed extension example](../examples/ManagedExtensionExample/) for a
complete source example.
