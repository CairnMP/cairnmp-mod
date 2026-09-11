# Architecture

CairnMP separates contributor-facing code from game and transport integration.
Dependencies flow through stable contracts; engine types never leak into features
or public extensions.

## Contents

- [Dependency map](#dependency-map)
- [Layer responsibilities](#layer-responsibilities)
- [Inbound packet flow](#inbound-packet-flow)
- [Enforced boundaries](#enforced-boundaries)
- [Build and deployment](#build-and-deployment)
- [Folder conventions](#folder-conventions)
- [Naming conventions](#naming-conventions)
- [Fatal errors and privacy](#fatal-errors-and-privacy)

## Dependency map

```text
External mods ──> CairnMultiplayer.Api ──> Internal/Extensions
                                         │
Features ──────> Framework ──────────────┤
   │                │                    │
   └──────────> GameApi <──── Internal/Game adapters

Bootstrap ──> Framework + GameApi + Internal
Internal/Networking ──> CairnMultiplayerShared
                     + Internal/Extensions
                     + Internal/Diagnostics
```

> [!IMPORTANT]
> `Features` may use `Framework` and `GameApi`. It must never depend directly on
> Unity, IL2CPP, Steam, Harmony, MelonLoader, `Bootstrap`, or `Internal`.

## Layer responsibilities

| Layer | Responsibility | Must not expose |
| --- | --- | --- |
| `CairnMultiplayerShared` | Packets, protocol constants, and pure validation | Mod, Unity, Steam, or IL2CPP dependencies |
| `CairnMultiplayer.Api` | Public managed extension contracts | Engine or transport types |
| `Framework` | Feature lifecycle and typed multiplayer channels | Native implementation details |
| `GameApi` | Safe in-mod façade for game operations | Engine or transport types |
| `Features` | Declarative multiplayer capabilities | Direct infrastructure access |
| `Internal` | Unity, IL2CPP, Steam, Harmony, and Cairn implementations | Public types |
| `Bootstrap` | Composition, callback wiring, and lifecycle ownership | Service-locator access from runtime services |

## Inbound packet flow

```text
Steam callback
    ↓
NetworkManager transport pump
    ↓
NetworkManager.PacketDispatch
    ├── extension packets
    ├── session packets
    ├── player packets
    └── world packets
    ↓
events / adapters / features
```

`PacketValidation` belongs to the shared protocol project. Both clients and the
authoritative host must call it before accepting untrusted positions, frames,
bones, weather, or pitons.

Transport code routes bytes. Domain-specific partial files deserialize and apply
packets. Networking reports game-facing actions as events consumed by `Bootstrap`;
it never calls Unity, `GameApi`, game adapters, or UI implementations directly.

## Enforced boundaries

The build and architecture tests enforce these rules:

- diagnostic `CMP003` rejects feature references to `Internal`, `Bootstrap`,
  Unity, IL2CPP, Steam, Harmony, MelonLoader, and the lower-level extension API;
- engine imports are rejected outside `Internal` and `Bootstrap`;
- unsafe `GameApi` dependencies and bootstrap service-locator access are rejected;
- patch lifecycles must install and uninstall symmetrically;
- every file under `Features` must declare a feature;
- types declared under `Internal` must not be public;
- dependencies stay explicit in each source file—no project-wide infrastructure
  import may hide coupling direction.

## Build and deployment

Normal builds never deploy. `DeployMod` defaults to `false`; set
`-p:DeployMod=true` only when intentionally installing a development build into
the local game.

| Project | Runtime | Game references | Primary coverage |
| --- | ---: | ---: | --- |
| `CairnMultiplayer.Tests` | .NET 10 | Not required | Protocol, architecture, generator, authority, diagnostics |
| `CairnMultiplayerMod.Tests` | .NET 6 | Required | Framework, gameplay, panels, extensions |

`CairnMultiplayer.sln` is the only solution. CI runs the portable project with
locked dependencies. Automated checks also pass `DeployMod=false`, and the mod
test project forwards that property to its project reference.

## Folder conventions

- Create a domain folder only when it contains at least three source files.
- Keep architectural layers separate even when a layer is small.
- Keep `Internal/Extensions` as the managed extension runtime boundary.
- Keep `Internal/Polyfills.cs` in its required
  `System.Runtime.CompilerServices` namespace.
- Place features directly in `Features/` under
  `CairnMultiplayerMod.Features`.
- Place native implementations under `Internal/Game`; larger domains such as
  `Players`, `Roping`, `Bivouac`, `MainMenu`, and `World` may retain folders.
- Keep all `NetworkManager` partials under `Internal/Networking`.
- Separate packet dispatch, deserialization, and packet application.

For smaller domains, make the filename carry the responsibility—for example,
`ChatController.cs`, `TimeInterop.cs`, or `WeatherInterop.cs`.

## Naming conventions

| Suffix | Responsibility |
| --- | --- |
| `Feature` | Declarative gameplay capability using `Framework` and `GameApi` |
| `Interop` | Access to native game or engine APIs |
| `Adapter` | Managed contract implemented over integration code |
| `Patch` | Installable and removable Harmony behavior change |
| `Diagnostics` | Observation and reporting without concealing failures |
| `Controller` | Coordination for one gameplay or UI domain |
| `Manager` | Ownership of a collection or subsystem resources |
| `Broadcaster` | Capture and publication of recurring state |
| `Service` | Cohesive capability shared by multiple callers |
| `Flow` | A process sequenced through multiple stages |
| `Gate` | A decision about whether an operation may proceed |

> [!CAUTION]
> Do not mass-rename compatibility contracts. Preserve `AssemblyName`,
> `MelonInfo`, preference categories, Harmony IDs, protocol IDs, and the public
> namespace `CairnMultiplayer.Api`.

## Fatal errors and privacy

Diagnostics are local-only. The diagnostics layer contains no HTTP client,
telemetry endpoint, machine hash, or automatic upload path. Recoverable
exceptions are deduplicated into a local session log and do not stop the mod.

Fatal errors follow one controlled path:

```text
fatal exception
    ↓
stop feature, UI, Steam, and network activity
    ↓
create UserData/CairnMultiplayer/Crashes/CairnMP-crash-*.zip
    ↓
show the local archive path in game
    ↓
close Cairn on request or after 30 seconds
```

The archive contains a readable report, a privacy notice, and bounded tails of
available CairnMP, MelonLoader, and Unity logs. It stays on the player’s computer
until they choose to share it.

| Local diagnostic data | Retention limit |
| --- | ---: |
| Session logs | 10 files / 20 MiB |
| Crash archives | 5 files / 100 MiB |

Architecture tests prevent network APIs and machine identifiers from being added
under `Internal/Diagnostics`.
