# CairnMP managed extension API

`CairnMultiplayer.Api` lets another MelonLoader mod take part in a CairnMP
session without touching Steam, peers or packets. Your mod declares typed
commands, replicated state and events. CairnMP negotiates compatibility when a
player joins, routes every command to the host, and replicates what the host
commits.

> [!NOTE]
> The public API version is `MultiplayerApi.Version == 1`. It is independent of
> both the CairnMP release version and the wire protocol version.

To start a new mod, clone the
[cairnmp-mod-example](https://github.com/CairnMP/cairnmp-mod-example)
repository: it builds a working integration you can rename and extend. The
[source example](../../examples/ManagedExtensionExample/) in this repository is
a shorter, single-file version.

## Contents

- [Reference CairnMP](#reference-cairnmp)
- [Register an extension](#register-an-extension)
- [Choose the right primitive](#choose-the-right-primitive)
- [Send authoritative commands](#send-authoritative-commands)
- [Replicate state](#replicate-state)
- [Publish transient events](#publish-transient-events)
- [Understand transactions](#understand-transactions)
- [Stay on the game thread](#stay-on-the-game-thread)
- [Serialize payloads](#serialize-payloads)
- [Observe sessions and players](#observe-sessions-and-players)
- [Safety defaults](#safety-defaults)
- [Checklist before you publish](#checklist-before-you-publish)

## Reference CairnMP

The API ships inside `CairnMultiplayerMod.dll`, in the game's `Mods` folder once
CairnMP is installed. Reference it without copying it into your build output,
next to MelonLoader:

```xml
<ItemGroup>
  <Reference Include="CairnMultiplayerMod">
    <HintPath>$(CairnDir)/Mods/CairnMultiplayerMod.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="MelonLoader">
    <HintPath>$(CairnDir)/MelonLoader/net6/MelonLoader.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

Target `net6.0`: MelonLoader hosts mods in .NET 6. Never ship
`CairnMultiplayerMod.dll` or `CairnMultiplayerShared.dll` with your mod; players
get them from CairnMP.

### Optional or required dependency

If your mod must also run without CairnMP, keep every `CairnMultiplayer.Api`
reference in a separate class, and call it from a method that catches the
missing assembly:

```csharp
[assembly: MelonOptionalDependencies("CairnMultiplayerMod")]

public override void OnInitializeMelon()
{
    try { MultiplayerIntegration.Register(); }
    catch (FileNotFoundException) { LoggerInstance.Msg("CairnMP not installed: solo only."); }
}

internal static class MultiplayerIntegration
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Register() { /* every CairnMultiplayer.Api call lives here */ }
}
```

The runtime resolves `CairnMultiplayerMod` when it first compiles
`MultiplayerIntegration.Register`. Without CairnMP, that call throws, and your
mod keeps running solo. `NoInlining` stops the JIT from compiling the API calls
into `OnInitializeMelon`, outside the `try`. If your mod is useless without multiplayer, use
`[assembly: MelonAdditionalDependencies("CairnMultiplayerMod")]` instead, and
let MelonLoader refuse to load it when CairnMP is missing.

## Register an extension

Register once, during your mod's initialization, before the player hosts or
joins a lobby:

```csharp
using CairnMultiplayer.Api;

var extension = MultiplayerApi.RegisterExtension(new ExtensionRegistration(
    "com.example.shared-goals",
    new Version(1, 2, 0))
{
    Requirement = ExtensionRequirement.Optional,
    MinimumPeerVersion = new Version(1, 0, 0),
    MaximumPeerVersion = new Version(1, 9, 99),
});
```

`RegisterExtension` throws when:

- the ID is not 3 to 64 lowercase ASCII characters, starting with a letter or a
  digit and using only letters, digits, `.`, `-` and `_`;
- `Version` is missing, or `MinimumPeerVersion` is greater than
  `MaximumPeerVersion`;
- the player is already in a lobby;
- the same ID is already registered.

Use a reverse-domain ID you own. `cairnmp.*` IDs are taken by CairnMP itself.

### Compatibility

When a player joins, the host compares its manifest with the joining player's.
Each side's range must accept the other side's version: the host's
`MinimumPeerVersion`/`MaximumPeerVersion` must accept the client's `Version`,
and the client's range must accept the host's. An empty bound accepts any
version.

| Case | `Optional` | `Required` |
| --- | --- | --- |
| Both sides compatible | Enabled for that player | Enabled for that player |
| Missing on the client | Player joins, extension off for them | Host rejects the player |
| Missing on the host | Player joins, extension off for them | Host rejects the player (when the client requires it) |
| Versions incompatible | Player joins, extension off for them | Host rejects the player |

The host then tells every admitted client which extensions are enabled for
each player. Check `extension.IsEnabledForPlayer(playerId)` before you show a
player something that depends on an optional extension.

Bump your extension `Version` when you change a contract, and narrow the peer
range when the change is incompatible: renaming a command, state or event, or
changing a payload's shape, breaks players on the old version.

## Choose the right primitive

| Primitive | Use it when… | Replayed to late joiners? |
| --- | --- | ---: |
| Command | A player asks the host to validate and apply an intention. | No |
| State | A current value must stay true, including for players who join later. | Yes |
| Event | Peers need a one-off notification, sound or effect. | No |

Command, state and event IDs follow the extension ID rules, from 1 to 64
characters, and must be unique within the extension. Registering the same ID
twice throws. Register them all at startup, right after `RegisterExtension`:
every peer must know the same contracts.

## Send authoritative commands

A command carries a player's intent. Only the host runs its handler, on the game
thread:

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
```

`context.Sender` is the player who sent the command; a client cannot claim to
be someone else. `context.Request` comes from that player's machine: treat it
as untrusted, check every value, and call `Reject` rather than throwing.

`SendAsync` returns the host's decision:

```csharp
Task<CommandResult> pending = addScore.SendAsync(2);
```

| `CommandStatus` | Meaning |
| --- | --- |
| `Committed` | The host accepted the command and published its effects. |
| `Rejected` | The handler called `Reject`, or CairnMP refused the command: unknown command, extension off for that player, payload too large, rate limit reached. `Reason` says which. |
| `Failed` | The payload could not be serialized or deserialized, or the handler threw. |
| `TimedOut` | The host did not answer within 10 seconds. |
| `Unavailable` | Not in a session, the session ended while waiting, or the extension was disabled for the session. |

When the local player is the host, `SendAsync` runs the handler immediately
and returns a completed task. On a client, the task completes when the host's
answer arrives. Read [Stay on the game thread](#stay-on-the-game-thread)
before you await it.

The host can also commit an update on its own initiative, for example to reset
a value when its session starts:

```csharp
if (MultiplayerApi.IsHost)
    extension.Commit(context => context.Set(score, 0));
```

`Commit` returns a `CommandResult` right away. It returns `Unavailable` on a
client.

## Replicate state

The host keeps state and replays it automatically to players who join later:

```csharp
score.Changed += change =>
{
    if (!change.Removed)
        Log($"Score revision {change.Revision}: {change.Value}");
};
```

- `context.Set(state, value)` writes global state, and `context.Remove(state)`
  clears it.
- `context.SetForPlayer(state, playerId, value)` and `RemoveForPlayer` write
  state scoped to one CairnMP player ID. `change.ScopePlayerId` is that ID, or
  `0` for global state.
- Player-scoped state is removed automatically, with a `Changed` notification,
  when that player leaves.
- Every peer reads its latest copy with `TryGet` or `TryGetForPlayer`.
  Updates arrive in order; a stale revision is ignored.
- When the session ends, every copy is cleared without a `Changed`
  notification. Subscribe to `MultiplayerApi.SessionEnded` to reset your own
  view.

Only a command handler or `extension.Commit` on the host can change state.

## Publish transient events

Events are not kept. Use them for effects that a player joining later does not
need to see:

```csharp
scored.Received += message =>
    Log($"Player {message.SourcePlayerId} scored {message.Payload}");
```

`SourcePlayerId` is the player whose command produced the event, or the host
for `Commit`. Events reach every peer that has the extension enabled,
including the host and the sender.

Use replicated state instead whenever a player joining later must know the
current value.

## Understand transactions

`Set`, `Remove`, `Broadcast` and `AfterCommit` stage changes. CairnMP publishes
nothing until the handler returns, then applies states, then events, then the
`AfterCommit` actions, in the order you staged them.

The whole transaction is discarded when:

- the handler calls `Reject`;
- the handler throws;
- a staged payload fails to serialize, or is larger than 48 KiB.

Cairn or Unity changes cannot be rolled back. Make them in `AfterCommit`, so
they only happen once the managed state is committed:

```csharp
context.AfterCommit(() => ShowBanner("Goal complete!"));
```

`AfterCommit` runs on the host only. To change the game on every peer, react to
the state change or the event in each peer's `Changed` or `Received` handler.

An exception in an `AfterCommit` action is caught and logged, but cannot undo
the committed state.

### The circuit breaker

Each exception from a handler, a `Commit` callback, an `AfterCommit` action, or
a payload that fails to deserialize counts as one failure for your extension.
A successful command resets the count. After three failures in a row, CairnMP
disables your extension on that peer for the rest of the session, and
`MultiplayerApi.ExtensionDisabled` fires there with your extension ID and a
reason. A disabled host answers further commands with `Unavailable`; a disabled
client ignores the extension's state and events. Exceptions thrown by your own
`Changed` and `Received` subscribers are logged but do not count.

Validate input and `Reject` it instead of throwing, so one bad request cannot
switch your extension off for everyone.

## Stay on the game thread

CairnMP calls command handlers, `Changed`, `Received` and the
`MultiplayerApi` session events on the game thread. You may touch Cairn and
Unity objects there.

An `await` on `SendAsync` is different. MelonLoader does not provide a Unity
`SynchronizationContext`, so the code after `await` can resume on a worker
thread. Touching a Unity object there can crash the game natively. Keep the
task and read it from your `OnUpdate` instead:

```csharp
private Task<CommandResult> _pending;

public void RequestPoint() => _pending ??= addScore.SendAsync(1);

public override void OnUpdate()
{
    if (_pending is not { IsCompleted: true }) return;
    var result = _pending.Result;
    _pending = null;
    if (!result.Committed)
        LoggerInstance.Warning($"Rejected: {result.Reason}");
}
```

Logging from a worker thread is safe. Anything that touches the game is not.

## Serialize payloads

Payloads use `PayloadCodec<T>.Json` by default, built on `System.Text.Json`
with its default options: only public properties are serialized, and names are
case-sensitive. Plain classes with public `get`/`set` properties are the
simplest payloads. Use a custom codec for a compact format or explicit schema
migration:

```csharp
var codec = new PayloadCodec<MyPayload>(Serialize, Deserialize);
var command = extension.RegisterCommand("custom", Handle, codec);
```

`Serialize` turns a value into bytes, and `Deserialize` turns bytes back into
a value, or throws when the bytes are invalid. A deserialized payload can still
hold any value: a missing JSON property becomes its default, and a modified
client can send numbers far out of range. Check values in the handler.

> [!IMPORTANT]
> Every serialized payload, command, state or event, is limited to **48 KiB**.
> Keep state small: it is resent in full on every change and to every late
> joiner.

## Observe sessions and players

| Member | Purpose |
| --- | --- |
| `IsConnected`, `IsHost` | Whether the local player is in a session, and whether they host it. |
| `LocalPlayer`, `Players` | The local player and the current roster. |
| `SessionReady` | The local player is in a session and the API can be used. |
| `SessionEnded` | The session is over; pending commands completed as `Unavailable` and state was cleared. |
| `PlayerJoined`, `PlayerLeft` | Roster changes. |
| `ExtensionDisabled` | The circuit breaker disabled an extension on this peer. |

`MultiplayerPlayer.Id` is a CairnMP player ID, valid for the current session
only. It is not a Steam ID; CairnMP never exposes Steam IDs to extensions.

## Safety defaults

- All extension traffic is reliable and goes through the host.
- Commands time out locally after 10 seconds without an answer from the host.
- A player may send at most 120 calls to the same command every 10 seconds;
  further calls are `Rejected` until the window ends.
- Unknown commands and disabled extensions are rejected without ending the
  session.
- Exceptions in your code are isolated from CairnMP and from other extensions.
- Steam IDs, peers, packet writers and transport objects are not exposed.

## Checklist before you publish

- [ ] The extension ID is yours, and the version range matches what you tested.
- [ ] Every command handler checks its request and rejects bad values.
- [ ] Game changes happen in `AfterCommit`, `Changed` or `Received`, never
      after an `await`.
- [ ] Late joiners get what they need from state, not from events.
- [ ] Your mod loads and plays solo without CairnMP, or declares it as a
      required dependency.
- [ ] You tested with two clients: host and client, a late joiner, a player
      leaving mid-action.
