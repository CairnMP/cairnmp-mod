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

Normal mod builds deploy by default. Automated builds and tests must set
`DeployMod=false`; the mod test project already passes that property through its project
reference, so running tests does not mutate the installed game.

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
