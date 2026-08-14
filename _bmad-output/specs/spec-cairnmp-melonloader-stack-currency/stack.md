# Stack: the pinned chain and its ceilings

Companion to `SPEC.md`. Everything here is a fact about what this project may and may not move.

## The dependency chain and who owns each link

| Link | Version | Owner | This project's lever |
|---|---|---|---|
| MelonLoader | v0.7.3 (2026-05-14) | LavaGang | None — whichever the player installed |
| .NET Desktop Runtime | 6.0 | Player's machine, required by the loader | None |
| Il2CppInterop | 1.5.3 current; loader bundles `1.5.1-ci.845` | BepInEx | None — supplied by the loader |
| HarmonyX | 2.16.1 | BepInEx | None — supplied by the loader |
| Target framework | `net6.0` × 4 projects | This repo | Technically ours; pinned by the loader |
| Test tooling | see below | This repo | Ours, up to a ceiling |

Two maintainers, not one: LavaGang ships the loader, BepInEx ships the interop and patching layers. Both are active, but a stall at either end pins this project and there is no lever on either.

The three `<Reference Include>` entries pointing at `$(MelonLoaderNet6Dir)` are correct as written. A mod referencing `0Harmony.dll` and `Il2CppInterop.Runtime.dll` out of the loader's folder consumes whatever the installed MelonLoader ships, so "upgrade Il2CppInterop" is not an action this project can take.

## Test tooling ceilings

| Package | CairnMP has | net6.0 ceiling | First version that drops net6.0 |
|---|---|---|---|
| `xunit` | 2.9.2 (2024-09-27) | **2.9.3** (2025-01-08) | v3 line entirely (floors at net8.0) |
| `Microsoft.NET.Test.Sdk` | 17.11.1 | **17.13.0** | 17.14.0 |
| `xunit.runner.visualstudio` | 2.8.2 | **3.0.2** (2025-02-07) | 3.1.0 (2025-05-03) |

These are ceilings imposed by the net6.0 pin, not steps toward currency. The whole xunit v2 line is marked legacy and deprecated on NuGet — security updates only, feature work moved to v3, and no release at all since 2.9.3. That is inherited from the loader's runtime pin, not a consequence of any decision made in this repo.

The runner jump is larger than it looks: 2.8.2 → 3.0.2 crosses a major version. It is safe because the VSTest adapter runs .NET and .NET Core projects from xUnit.net **v2 and v3** — the 3.x runner is backwards compatible with v2 test projects, so runner 3.0.2 paired with xunit 2.9.3 on net6.0 is a supported combination. Verify this still holds before going further; it is the assumption the jump rests on.

### The AssetTargetFallback trap — it happens twice

**Do not exceed Test.Sdk 17.13.0, and do not exceed runner 3.0.2.** Both packages drop net6.0 the same way, and neither failure looks like what it is.

Test.Sdk 17.14.0 does not hard-fail restore on a net6.0 project. It silently falls back to the `net462` asset via `AssetTargetFallback` and then fails at *runtime* with "Could not find testhost" — an error that reads as a broken test setup rather than an unsupported package. The cause is a TFM bump in the shipped assets: 17.13.0 ships `netcoreapp3.1` + `net462`, 17.14.0 ships `net8.0` + `net462`. A later 17.14.1 added an explicit warning.

The runner has the identical shape. 3.0.2 explicitly targets net6.0 + net472. 3.1.0 bumped its explicit targets to net8.0 + net472, leaving net6.0 present only as a *computed* compatibility entry. Current is 3.1.5 (2025-09-27), with a 4.0.0 prerelease line open.

Test.Sdk's current release (18.8.1) likewise lists net6.0 only as a computed entry — the explicitly included targets are .NET 8.0, .NET Core 2.0, .NET Standard 2.0 and .NET Framework 4.6.2.

**The generalisation worth carrying:** on a net6.0 project, "NuGet says it's compatible" is not evidence. Check for the words *"This package targets…"* against net6.0 specifically; a computed entry means the package will restore and then fail later. Two of the three test packages already behave this way, so the pins are the durable deliverable here and the version bumps are the one-off.

## Why full-solution CI is blocked

`CairnMultiplayerShared.Tests` references only `CairnMultiplayerShared` — no game assemblies anywhere in its graph, so it builds and runs on a stock runner today. `CairnMultiplayerMod` and `CairnMultiplayerMod.Tests` cannot, because generating their Il2Cpp reference assemblies requires a local Cairn install, a built CairnLoader from the sibling `cairnmp-launcher` repo, and launching the actual game for up to five minutes to dump the assemblies.

The ecosystem's standard workaround is a stripped-assembly NuGet feed (the BepInEx upload service; JetBrains Refasmer as the stripping tool). That route is gated on a licensing question, not a technical one — see `accepted-risks.md`.

## Re-check cadence

Version claims decay fast; every fact above was verified directly on 2026-08-12. A single pass over the six package pages (MelonLoader, Il2CppInterop, HarmonyX, xunit, Microsoft.NET.Test.Sdk, xunit.runner.visualstudio) refreshes all of them at once and is roughly a ten-minute job. Compatibility claims — the net6.0 requirement, the loader-supplied chain, both test-package ceilings — hold longer.

Two dates that will not change and need no re-check: .NET 6 end of support (2024-11-12, historical fact) and xunit v3's net8.0 floor.

**A method warning for whoever refreshes this:** GitHub's HTML release pages misreported MelonLoader release dates by one and two years across three separate fetches in the source research. Use the GitHub API or the `releases.atom` feed instead. Note the atom feed exposes `<updated>`, not original publication time, so older cadence entries are approximate.
