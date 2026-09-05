# Architecture

CairnMP separates stable contributor code from game and transport integration.
Dependencies flow inward through contracts; lower-level code never leaks engine types into
features or public extensions.

```text
External mods ──> CairnMultiplayer.Api ──> Internal/Extensions
                                         │
Features ──────> Framework ──────────────┤
   │                │                    │
   └──────────> GameApi <──── Internal/Game adapters

Bootstrap ──> Framework + GameApi + Internal
Internal/Networking ──> CairnMultiplayerShared + Internal/Extensions + Internal/Diagnostics
```

## Project responsibilities

- `CairnMultiplayerShared` owns serialized packets, protocol constants and pure validation.
  It has no dependency on the mod, Unity, Steam or IL2CPP.
- `CairnMultiplayer.Api` is the public managed extension surface. Its public signatures expose
  only managed contract types.
- `Framework` owns feature lifecycle and typed multiplayer channels.
- `GameApi` is the safe in-mod façade for game operations. It exposes no engine or transport
  types.
- `Features` contains feature declarations only. A feature talks to Cairn through `GameApi`
  and declares networking through `FeatureBuilder`.
- `Internal` contains every implementation coupled to Unity, IL2CPP, Steam, Harmony or Cairn.
  No type declared there is public.
- `Bootstrap` is the composition root. It creates adapters and services, wires callbacks and
  drives their lifecycle.

## Inbound network flow

```text
Steam callback
    -> NetworkManager transport pump
    -> NetworkManager.PacketDispatch
        -> extension packets
        -> session packets
        -> player packets
        -> world packets
    -> events/adapters/features
```

`PacketValidation` lives in the shared protocol project. Both client and authoritative-host
paths must call it before accepting untrusted positions, frames, bones, weather or pitons.
Transport code routes bytes; domain-specific partial files apply the resulting packet.
Networking reports game-facing actions as events consumed by `Bootstrap`; it does not call
Unity, `GameApi`, game adapters or UI implementations directly.

## Enforced rules

- `CMP003` rejects feature references to `Internal`, `Bootstrap`, Unity, IL2CPP, Steam,
  Harmony, MelonLoader and the lower-level managed extension API.
- Architecture tests reject engine imports outside `Internal`/`Bootstrap`, unsafe `GameApi`
  dependencies, bootstrap service-locator access, asymmetric patch lifecycles,
  non-feature files under `Features`, and public types under `Internal`.
- Dependencies are explicit at each source file; no project-wide infrastructure import
  hides the direction of coupling.

## Build and deployment

Normal builds never deploy: `DeployMod` defaults to `false`. Set `-p:DeployMod=true`
explicitly to install a development build into the local game. Automated checks also
pass `DeployMod=false`; the mod test project passes it through its project reference.

`CairnMultiplayer.sln` is the only solution. `CairnMultiplayer.Tests` runs on .NET 10
without game assemblies and owns the protocol, architecture, generator and source-linked
authority/diagnostic suites. `CairnMultiplayerMod.Tests` runs on .NET 6 and requires the
proprietary references. CI runs the portable project with locked dependencies.

## Folder and naming conventions

A domain folder exists only when it contains at least three source files. Smaller groups
live in their parent; filenames carry the domain. Keep the architectural layers above
separate even when a layer is small. In particular, `Internal/Extensions` remains its own
boundary for the managed extension runtime. `Internal/Polyfills.cs` keeps its required
`System.Runtime.CompilerServices` namespace.

Features live directly in `Features/`, in namespace `CairnMultiplayerMod.Features`.
Their implementations live under `Internal/Game`, with larger domains such as `Players`,
`Roping`, `Bivouac`, `MainMenu` and `World` retaining folders. Small domains are identified
by filenames such as `ChatController.cs`, `TimeInterop.cs` and `WeatherInterop.cs`.
All `NetworkManager` partials live together under `Internal/Networking`; packet dispatch
stays separate from deserialization and packet application.

Use these suffixes when naming new code:

| Suffix | Responsibility |
|---|---|
| `Feature` | Declarative gameplay capability using Framework and GameApi |
| `Interop` | Access to native game or engine APIs |
| `Adapter` | Implements a managed contract over integration code |
| `Patch` | Installs and uninstalls a Harmony behavior change |
| `Diagnostics` | Observes and reports behavior without concealing failures |
| `Controller` | Coordinates interactions for one gameplay or UI domain |
| `Manager` | Owns a collection of entities or a subsystem's resources |
| `Broadcaster` | Captures and publishes recurring state updates |
| `Service` | Provides a cohesive capability used by multiple callers |
| `Flow` | Sequences a process through multiple stages |
| `Gate` | Decides whether another operation may proceed |

Existing names are not a reason for mass renaming. Preserve `AssemblyName`, `MelonInfo`,
preference category names, Harmony IDs, protocol IDs and the public namespace
`CairnMultiplayer.Api`: these are compatibility contracts with installed clients and extensions.

## Fatal errors and privacy

Diagnostics are local-only. The diagnostic layer has no HTTP client, telemetry endpoint,
machine hash or automatic upload path. Recoverable exceptions are deduplicated into a local
session log and do not stop the mod.

An exception explicitly classified as fatal follows one controlled path:

```text
fatal exception
    -> stop feature, UI, Steam and network activity
    -> create UserData/CairnMultiplayer/Crashes/CairnMP-crash-*.zip
    -> show the archive path in an in-game crash screen
    -> close Cairn on request or after 30 seconds
```

The ZIP contains a readable crash report, a privacy notice and bounded tails of the available
CairnMP, MelonLoader and Unity logs. It stays on the player's computer until the player
chooses to share it. Storage is bounded to 10 session logs / 20 MiB and 5 crash archives /
100 MiB. Architecture tests prevent network APIs and machine identifiers from being added
under `Internal/Diagnostics`.
