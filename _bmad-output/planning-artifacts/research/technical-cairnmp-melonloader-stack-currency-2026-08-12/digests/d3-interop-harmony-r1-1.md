# Digest — D3 Il2CppInterop & HarmonyX currency — round 1

Question owned: are the interop and patching libraries current, and are their versions CairnMP's to choose?
Accessed: 2026-08-12

## Claims

| # | Claim | Source | Publisher | Pub date | Confidence | Class |
|---|---|---|---|---|---|---|
| 19 | Il2CppInterop.Runtime latest is **1.5.3**, published 2026-06-20 (4,188 downloads) | https://www.nuget.org/packages/Il2CppInterop.Runtime | NuGet / BepInEx | 2026-06-20 | high | version |
| 20 | Il2CppInterop.Runtime version history: 1.5.0 (2025-05-13), 1.5.1 (2025-09-02), 1.5.3 (2026-06-20) | https://www.nuget.org/packages/Il2CppInterop.Runtime | NuGet / BepInEx | 2026-06-20 | high | version |
| 21 | Il2CppInterop.Runtime targets **.NET 6.0** as its primary framework | https://www.nuget.org/packages/Il2CppInterop.Runtime | NuGet / BepInEx | 2026-06-20 | high | compatibility |
| 22 | Il2CppInterop is maintained under the **BepInEx** organisation | https://github.com/BepInEx/Il2CppInterop | BepInEx | accessed 2026-08-12 | high | ecosystem |
| 23 | HarmonyX latest is **2.16.1**, published 2026-03-30 | https://www.nuget.org/packages/HarmonyX | NuGet / BepInEx | 2026-03-30 | high | version |
| 24 | HarmonyX cadence: 2.13.0 (2024-06-12), 2.14.0 (2025-01-12), 2.15.0 (2025-09-02), 2.16.0 (2025-11-25), 2.16.1 (2026-03-30) | https://www.nuget.org/packages/HarmonyX | NuGet / BepInEx | 2026-03-30 | high | ecosystem |
| 25 | HarmonyX is owned/maintained by **BepInEx** and supports netstandard2.0, netfx3.5/4.5.2, and net5.0–net10.0 | https://www.nuget.org/packages/HarmonyX | NuGet / BepInEx | 2026-03-30 | high | compatibility |
| 26 | MelonLoader v0.7.3 bundles Il2CppInterop at `1.5.1-ci.845` — a CI build, not the tagged NuGet 1.5.1 | https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3 | LavaGang | 2026-05-14 | high | version |

## The decisive structural finding

MelonLoader's own NuGet package declares Il2CppInterop (Common, Generator, HarmonySupport, Runtime) and HarmonyX as **dependencies of the net6.0 group** (D1 claim #5, same source). Combined with #26, this means the interop and patching library versions are **supplied by the loader**, not selected by the mod. A mod that references `0Harmony.dll` and `Il2CppInterop.Runtime.dll` out of the MelonLoader folder consumes whatever the installed loader ships.

Consequence: "upgrade Il2CppInterop / HarmonyX" is not an available action for a mod project. The only lever is the MelonLoader version the player has installed.

## Version gap observed

MelonLoader v0.7.3 (2026-05-14) predates Il2CppInterop 1.5.3 (2026-06-20) by ~5 weeks, so the current loader necessarily ships something older than current interop. This is normal lag, not neglect.

## Ecosystem health

Both libraries sit under BepInEx with steady release cadence through 2025–2026 (#24, #20). No abandonment signal. Note this is a *different* organisation from LavaGang (MelonLoader) — the modding stack has two independent maintainers in the chain.

## Leads worth chasing

- Whether MelonLoader pins interop via CI builds routinely (`-ci.845`) — an ecosystem-health nuance, low value for this decision.

## Looked for and could not find

- A breaking-change list between Il2CppInterop 1.5.1 and 1.5.3, or between HarmonyX 2.15 and 2.16.1. Not pursued further because the structural finding above makes it moot for CairnMP — the mod does not choose these versions.
