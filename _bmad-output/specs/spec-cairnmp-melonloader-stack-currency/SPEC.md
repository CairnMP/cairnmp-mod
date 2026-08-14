---
id: SPEC-cairnmp-melonloader-stack-currency
companions:
  - stack.md
  - accepted-risks.md
  - ../../../AGENTS.md
sources:
  - ../../planning-artifacts/research/technical-cairnmp-melonloader-stack-currency-2026-08-12/research.md
---

> **Canonical contract.** This SPEC and the files in `companions:` are the complete, preservation-validated contract for what to build, test, and validate. Source documents listed in frontmatter are for traceability — consult them only if you need narrative rationale or prose color this contract intentionally omits.

# CairnMP stack: verification over currency

## Why

A mandate the project cannot discharge, plus a gap it can. CairnMP parses untrusted packets from remote peers on the .NET 6 Desktop Runtime, which reached end of support on 2024-11-12 and receives no further security updates. That runtime is not the project's to move: MelonLoader v0.7.3 — current and actively maintained — hosts IL2CPP mods on .NET 6, so a net8 assembly would not load at all. Every "upgrade the framework" instinct dies there, and the same pin propagates outward, putting xunit v3 out of reach and making Il2CppInterop and HarmonyX loader-supplied rather than chosen. What remains reachable is the opposite of currency: **verification**. The packet-parsing code that carries the security exposure is also the only code in the repo that builds with no game assemblies at all — so the cheapest available action and the highest-risk surface are the same code, and today nothing verifies it on any push.

## Capabilities

- **CAP-1**
  - **intent:** Every push verifies the packet and protocol surface — codecs, command parser, quaternion codec, rope-clip logic, server teleport, extension negotiator — without any proprietary file.
  - **success:** A CI job runs `dotnet test CairnMultiplayerShared.Tests -c Release` on a stock runner with no secrets and no game assemblies, and a deliberately broken codec test turns that job red.

- **CAP-2**
  - **intent:** A contributor following `scripts/generate-il2cpp-refs.ps1` is never instructed to do something the project forbids.
  - **success:** The script's completion message no longer tells the user to commit the generated assemblies, and no remaining line of it contradicts `.gitignore:2` or the README's non-redistribution statement.

- **CAP-3**
  - **intent:** The build surfaces its own SDK going end-of-life instead of letting it pass silently.
  - **success:** `CheckSdkVulnerabilities=true` is set and an end-of-life build SDK emits NETSDK1239. This guards the SDK, not the target framework — it will not fire on the net6.0 pin, by design.

- **CAP-4**
  - **intent:** Test tooling sits at the highest versions the net6.0 pin allows, and cannot silently drift past a ceiling whose failure mode is a runtime error rather than a restore error.
  - **success:** `xunit` at 2.9.3, `Microsoft.NET.Test.Sdk` at 17.13.0, `xunit.runner.visualstudio` at 3.0.2, `dotnet test CairnMultiplayerShared.Tests` still passing, and all three ceilings recorded where whoever bumps next will see them.

## Constraints

- `<TargetFramework>net6.0</TargetFramework>` stays in all four projects. MelonLoader hosts IL2CPP mods on the .NET 6 CoreCLR; a net8+ assembly cannot be loaded by it.
- CI must require no game assemblies, no secrets, and no Cairn install. Generating the Il2Cpp reference assemblies needs a local game install, a built CairnLoader from the sibling `cairnmp-launcher` repo, and an actual game launch taking up to five minutes — none of it automatable on a runner.
- Proprietary Cairn, Unity, and MelonLoader assemblies are never committed or published. `.gitignore:2` excludes `game-refs/`, the README asserts they cannot be redistributed, and the AGENTS.md policy repeats it.
- Il2CppInterop and HarmonyX versions are whatever the installed MelonLoader ships. The only lever is which loader the player has, so neither is a dependency this project can bump.
- `Microsoft.NET.Test.Sdk` must not exceed 17.13.0. 17.14.0 restores without error via `AssetTargetFallback`, then fails at runtime with "Could not find testhost" — a failure that reads as a broken test setup rather than an unsupported package.
- `xunit.runner.visualstudio` must not exceed 3.0.2. 3.1.0 bumped its explicit targets to net8.0 + net472, leaving net6.0 as a computed entry only — the same silent-failure shape as Test.Sdk 17.14.0.
- `xunit` stays on the v2 line. v3 floors at net8.0, which the pin puts out of reach.

## Non-goals

- Raising the target framework to net8.0, net9.0, or net10.0 in any project.
- Migrating to xunit v3.
- CI covering `CairnMultiplayerMod` or `CairnMultiplayerMod.Tests` — both need game assemblies that cannot reach a runner.
- Upgrading Il2CppInterop or HarmonyX. Not this project's to choose.
- Publishing stripped or publicized Cairn assemblies to any public feed. That is a licensing decision needing its own basis, not a technical step (see `accepted-risks.md`).

## Success signal

A push turns the packet-parsing surface from unverified to continuously verified: CI green on a stock runner that holds no proprietary file, and a deliberately corrupted packet-codec test turns it red. Separately, nothing left in the repo instructs a contributor to commit `game-refs/`.

## Assumptions

- The mod must keep loading under the MelonLoader v0.7.x releases players actually have installed, so the net6.0 pin is fixed for the life of this spec.
- CI runs on GitHub Actions, from the `origin` remote being GitHub. Note this repo is a fork with a separate `upstream` remote, which affects workflow triggers on fork PRs.

## Open Questions

None blocking. Publishing stripped Cairn assemblies — the only route that would ever make full-solution CI reachable — is a settled non-goal rather than an open question; see `accepted-risks.md`.
