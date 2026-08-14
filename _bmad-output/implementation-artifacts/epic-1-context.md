# Epic 1 Context: CairnMP stack — verification over currency

<!-- Compiled from planning artifacts. Edit freely. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Turn the packet-parsing surface from unverified into continuously verified, without moving the `net6.0` pin that MelonLoader imposes. The mod parses untrusted packets from remote peers on a .NET 6 runtime that stopped receiving security updates in November 2024, and that runtime is not this project's to move — MelonLoader hosts IL2CPP mods on .NET 6, so a net8 assembly would not load at all. Currency is therefore unreachable; verification is not. The packet-parsing code carrying the security exposure is also the only code in the repo that builds with zero game assemblies, so the highest-risk surface and the cheapest available action are the same code. This epic puts that code under CI on every push, makes the build report its own SDK going end-of-life, raises test tooling to the highest versions the pin allows with the ceilings recorded, and removes the one instruction in the repo that contradicts the non-redistribution policy.

## Stories

- Story 1.1: Add GitHub Actions CI for the protocol test suite
- Story 1.2: Stop generate-il2cpp-refs.ps1 telling contributors to commit game-refs
- Story 1.3: Surface end-of-life build SDKs via CheckSdkVulnerabilities
- Story 1.4: Raise and pin test packages to their net6.0 ceilings

## Requirements & Constraints

- Every push must verify the packet and protocol surface — codecs, command parser, quaternion codec, rope-clip logic, server teleport, extension negotiator. The bar is behavioural: a deliberately broken codec test must turn CI red. A green pipeline that never fails on a real defect does not satisfy this.
- CI must run on a stock runner with **no secrets, no game assemblies, no Cairn install**. Only the shared protocol test suite qualifies; it references only the shared library and nothing proprietary in its graph.
- The target framework stays `net6.0` in all four projects. Raising it to net8/9/10 is a non-goal, not a deferred task.
- Proprietary Cairn, Unity, and MelonLoader assemblies are never committed or published. Nothing in the repo — script output, docs, messages — may instruct a contributor to commit generated reference assemblies, since that contradicts both the ignore rules and the README's non-redistribution statement.
- The build must surface an end-of-life build SDK rather than letting it pass silently. This guards the SDK, not the target framework, so it will correctly stay quiet about the `net6.0` pin — that is intended behaviour, not a misconfiguration to "fix".
- Test tooling must sit at its net6.0 ceilings and be pinned so a later bump cannot silently cross them: `xunit` 2.9.3, `Microsoft.NET.Test.Sdk` 17.13.0, `xunit.runner.visualstudio` 3.0.2. The pins are the durable deliverable; the version bumps are the one-off.

## Technical Decisions

- **The runtime pin is external and fixed.** MelonLoader supplies the .NET 6 host, and it also supplies Il2CppInterop and HarmonyX — the mod references those out of the loader's folder, so their versions are whatever the player's loader ships. Neither is a dependency this project can bump. The only lever on the whole lower stack is which loader the player installed.
- **xunit stays on the v2 line.** v3 floors at net8.0. Migration is a non-goal for the life of this pin.
- **The AssetTargetFallback trap governs the test-package ceilings.** Both `Microsoft.NET.Test.Sdk` above 17.13.0 and `xunit.runner.visualstudio` above 3.0.2 restore cleanly on a net6.0 project via fallback assets and then fail at *runtime* with "Could not find testhost" — a failure that reads as a broken test setup rather than an unsupported package. Consequently: a successful restore is not evidence, and "NuGet says it's compatible" is not evidence. Verification means actually running the shared test suite. When judging any future package for net6.0, look for an explicit target rather than a computed compatibility entry.
- **CI scope is deliberately partial.** Full-solution CI is blocked because generating the Il2Cpp reference assemblies requires a local game install, a build from a sibling launcher repo, and a real game launch of up to five minutes — none of it automatable. The ecosystem's workaround (a feed of stripped, publicized game assemblies) is gated on an unresolved licensing question, treated as a licensing decision first and a technical one second. It is out of scope, so CI over the mod project and its tests stays a non-goal.
- **The repo is a fork with a separate `upstream` remote.** This affects workflow trigger selection, particularly for fork PRs. CI is assumed to run on GitHub Actions.

## Cross-Story Dependencies

- **Story 1.1 must land and be green before Story 1.4.** The CI workflow is the safety net that verifies the package bump; without it, the bump's runtime failure mode is exactly the one that hides behind a clean restore.
- Stories 1.2 and 1.3 are independent of the others and of each other.
- Story 1.4 carries a sprint-planning decision beyond the spec: apply the same three pins to the mod test project as well as the shared one, even though only the shared suite is verifiable on a runner. Leaving the mod suite behind would reintroduce the drift the pins exist to prevent.
