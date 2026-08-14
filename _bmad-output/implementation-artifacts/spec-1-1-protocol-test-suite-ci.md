---
title: 'Story 1.1: Add GitHub Actions CI for the protocol test suite'
type: 'feature'
created: '2026-08-13'
status: 'in-review'
baseline_commit: 'e9dc6fdbe5c97030d923b3b1ea1930691b1f1327'
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `CairnMultiplayerShared` parses untrusted packets from remote peers on a .NET 6 runtime that stopped receiving security patches in November 2024, and nothing verifies it on any push. The runtime pin is not ours to move, so verification is the only reachable mitigation — and this is the one project in the repo that builds with zero proprietary files.

**Approach:** One GitHub Actions workflow that runs `dotnet test CairnMultiplayerShared.Tests -c Release` on a stock runner, scoped to that single project so no game assembly is ever needed. Prove it works in both directions: green on a correct tree, red on a deliberately broken codec test.

## Boundaries & Constraints

**Always:** Scope the test command to the `CairnMultiplayerShared.Tests` project path. Install a .NET 6 runtime explicitly — hosted runner images ship .NET 8/9/10 only, so the `net6.0` testhost cannot launch without it. Keep the job free of secrets, so it runs on any fork.

**Ask First:** Adding a `global.json` (pins the SDK for every contributor, not just CI). Adding NuGet caching. Adding any second job, matrix leg, or runner OS. Changing `Directory.Build.props`.

**Never:** Build or test solution-wide (`CairnMultiplayer.slnx`), invoke `scripts/check.sh`, or touch `CairnMultiplayerMod` / `CairnMultiplayerMod.Tests` — all of them require `game-refs/`, which is gitignored and cannot reach a runner. Do not change `<TargetFramework>`. Do not bump test package versions — that is Story 1.4, and this workflow is the safety net that will verify it.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Green path | Push to `develop`, tree correct | Job restores 2 projects, runs 63 tests, exits 0 | N/A |
| Red path | A codec assertion deliberately falsified | Job fails with that single named test failure | Failure is the expected signal |
| No game assemblies | Runner has no `game-refs/`, no Cairn install | Job still green — nothing in the graph needs them | N/A |
| Internal PR | PR opened from a branch in this repo to `develop` | Exactly one run for the SHA, not two | N/A |
| Fork-hostile env | No secrets available to the job | Job green; restore is anonymous against nuget.org | N/A |

</frozen-after-approval>

## Code Map

- `.github/workflows/` -- does not exist; this story creates it. `.github/` currently holds only `FUNDING.yml`.
- `CairnMultiplayerShared.Tests/CairnMultiplayerShared.Tests.csproj` -- the CI-viable suite. 3 NuGet refs; single `ProjectReference` at line 17 to `CairnMultiplayerShared`.
- `CairnMultiplayerShared/CairnMultiplayerShared.csproj` -- 11 lines, `net6.0`, zero package/project/assembly references. The graph terminates here — this is why CI is possible at all.
- `CairnMultiplayerShared.Tests/PacketCodecTests.cs:108` -- `PingPacketTests.ClientPingPlaced_RoundTrips`, a self-contained `[Fact]` asserting three floats. The chosen break candidate for the red proof.
- `Directory.Build.props:44` -- `WarningsAsErrors=CS8600;CS8601;CS8602;CS8603;CS8604`; a future nullable-flow warning becomes a hard CI failure. Read-only here.
- `Directory.Build.props:46` -- `NoWarn=NETSDK1138` already suppresses the "net6.0 is out of support" build warning. No CI-side flag needed.
- `Directory.Build.props:17-37` -- game/`LOCALAPPDATA` paths. Verified harmless when absent: all are plain string concatenation or `Exists()`-guarded, so they evaluate to dead strings rather than failing.
- `CairnMultiplayerMod/CairnMultiplayerMod.csproj:171-195` -- `CopyToMods` target, an unconditional `<Copy>` to the game folder. Would hard-fail on a runner. Never reached by a project-scoped test command — the reason scope discipline matters.
- `scripts/check.sh:21` -- runs `dotnet test CairnMultiplayer.slnx`, solution-wide. CI must not call this.

## Tasks & Acceptance

**Execution:**
- [x] `.github/workflows/ci.yml` -- create the workflow: a single `ubuntu-latest` job running `actions/checkout@v7`, `actions/setup-dotnet@v6` with `dotnet-version: 6.0.x`, then `dotnet test CairnMultiplayerShared.Tests -c Release`. Triggers: `push` on `develop`/`production`, `pull_request` targeting them, plus `workflow_dispatch`. Add a `concurrency` group keyed on workflow + ref with `cancel-in-progress` -- restricting `push` to long-lived branches is what stops an internal PR firing twice for one SHA.
- [x] Verify locally first -- run the test command as CI will, confirming 63 passing before spending a runner.
- [x] Prove green on a real runner -- push the branch and open a PR to `develop`, so the `pull_request` trigger fires the job.
- [x] Prove red -- push one commit falsifying the assertion in `PacketCodecTests.cs:108`, confirm the same PR check turns red naming that test, then revert it and confirm green returns. The revert must land before the story closes.

**Acceptance Criteria:**
- Given a runner with no game assemblies, no Cairn install and no secrets, when the workflow runs, then `dotnet test CairnMultiplayerShared.Tests -c Release` completes green.
- Given a deliberately falsified codec assertion, when the workflow runs, then the job fails and names that test.
- Given a PR opened from a branch in this repo to `develop`, when the checks run, then exactly one run exists for that SHA.
- Given the workflow file, when it is read, then it references no path under `game-refs/`, no secret, and no project other than `CairnMultiplayerShared.Tests`.

## Spec Change Log

## Design Notes

**Why install .NET 6 rather than roll forward.** Current runner images (`ubuntu-24.04`, `windows-2025`) ship .NET 8/9/10 SDKs and runtimes only — no .NET 6 of either kind. Building `net6.0` still works there (the ref pack restores from nuget.org), but *executing* the tests does not: `testhost.dll` requests `Microsoft.NETCore.App 6.0.0` and default roll-forward never crosses a major, so the run dies at test-host launch rather than at build. `DOTNET_ROLL_FORWARD=LatestMajor` would paper over that, and it is the wrong trade here: MelonLoader pins players to .NET 6, so a green CI proving the code works on .NET 10 tests a runtime no user will ever execute. The 6.0.x SDK install costs ~20-30s and carries the 6.0.36 runtime the testhost needs.

**Confidence this can be green.** The command was executed under a runner simulation: this machine carries no system .NET SDK, so an SDK was unpacked into a throwaway scratchpad directory (never installed onto the machine or added to PATH), and `LOCALAPPDATA` was redirected to a nonexistent directory. Result: 63/63 passed, exit 0, only the two shared projects restored. Stated precisely because "no SDK installed" would be self-contradictory — a build needs an SDK; the point is that nothing about the machine's game install or system state contributed.

**One external fragility, recorded not solved.** This depends on Microsoft continuing to serve EOL .NET 6 binaries. Precedent (3.1, 5.0) says they will. If that ever breaks, this workflow breaks with it — the fallback is a self-hosted runner, not a target-framework change.

## Verification

**Commands:**
- `dotnet test CairnMultiplayerShared.Tests -c Release` -- expected: all tests pass, 0 failed, exit 0 (63 at time of writing).
- `gh run list --workflow ci.yml -R yerayalfageme-glitch/cairnmp-mod` -- expected: after the red proof and its revert, a failing run followed by a passing one on the same PR.

**Manual checks:**
- Actions are disabled by default in a fork. Confirm the first run actually starts; if it does not, enable Actions on the repo before treating any absence of runs as a pass.

**Evidence (PR #5 against `develop`, repo `yerayalfageme-glitch/cairnmp-mod`):**
- Actions enabled on the repo (`{"enabled":true}`), and the first run genuinely started -- the manual check above passed rather than being assumed.
- Green: run `31778561382` on commit `ef194e0` -- all tests passed, 0 failed; log shows only the two shared projects restored, so nothing in the graph reached for a game assembly.
- Red: run `31778629722` on commit `6d20388`, which falsified one assertion in `ClientPingPlaced_RoundTrips` -- job failed naming `CairnMultiplayerShared.Tests.PingPacketTests.ClientPingPlaced_RoundTrips`, 1 failed / 62 passed.
- Green again: the break was reverted in `6e25d55` (tree byte-identical to `ef194e0`), runs `31778688304` and `31778770191` both green. The revert landed before the story closed.
- Exactly one run per SHA: every run carries `event=pull_request` and no SHA has two, confirming the `push` filter prevents an internal PR firing twice.
- Local pre-flight matched CI before a runner was spent: same command, .NET 6 SDK, all passing, exit 0, two projects restored.

**Not yet exercised:** the `push` trigger. Only `pull_request` has fired so far -- `push` on an integration branch first runs on merge. The job is identical either way, so the risk is low but it is unproven.
