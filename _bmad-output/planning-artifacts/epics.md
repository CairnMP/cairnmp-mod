# Epics: CairnMP stack — verification over currency

> **Derived artifact.** This file is a projection of
> `_bmad-output/specs/spec-cairnmp-melonloader-stack-currency/stories.yaml` into the
> epic/story headings `bmad-sprint-planning` parses. The SPEC and its companions
> remain the canonical contract; `stories.yaml` remains the story source of truth.
> When either changes, update this file and rerun sprint planning rather than
> editing story text here in isolation.
>
> Source: `SPEC-cairnmp-melonloader-stack-currency` (CAP-1 … CAP-4).

## Epic 1: CairnMP stack — verification over currency

**Goal.** Turn the packet-parsing surface from unverified into continuously verified,
and remove the one instruction in the repo that contradicts the non-redistribution
policy — all without moving the `net6.0` pin that MelonLoader imposes.

**Value delivered.** Every push checks the code that parses untrusted packets from
remote peers; the build reports its own SDK going end-of-life; test tooling sits at
the highest versions the pin allows, with the ceilings recorded so a later bump
cannot silently cross them.

**Scope boundary.** No target-framework change, no xunit v3, no CI over
`CairnMultiplayerMod` / `CairnMultiplayerMod.Tests` (both need game assemblies that
cannot reach a runner). See `accepted-risks.md` for the licensing question that
keeps full-solution CI out of scope.

### Story 1.1: Add GitHub Actions CI for the protocol test suite

Satisfies CAP-1: a workflow running `dotnet test CairnMultiplayerShared.Tests -c Release`
on a stock runner with no secrets. Must not build the solution or any project needing
game assemblies — see the CI constraint in SPEC.md.

**Acceptance.** The job runs on a stock runner with no secrets and no game assemblies,
and a deliberately broken codec test turns it red.

**Notes for the developer.** This repo is a fork with a separate `upstream` remote; pick
trigger events accordingly. Story 1.4 bumps test packages afterwards, so this workflow is
the safety net that verifies that change — get it green first.

### Story 1.2: Stop generate-il2cpp-refs.ps1 telling contributors to commit game-refs

Satisfies CAP-2: line 97's completion message tells the user to commit the generated
assemblies, contradicting `.gitignore:2` and the README. Fix the message and confirm no
other line in the script says the same.

**Acceptance.** The script's completion message no longer instructs the user to commit the
generated assemblies, and no remaining line of it contradicts `.gitignore:2` or the
README's non-redistribution statement.

### Story 1.3: Surface end-of-life build SDKs via CheckSdkVulnerabilities

Satisfies CAP-3: set `CheckSdkVulnerabilities=true` so an end-of-life build SDK emits
NETSDK1239. It guards the SDK, not the target framework, so it will not fire on the
`net6.0` pin — that is intended, not a misconfiguration.

**Acceptance.** `CheckSdkVulnerabilities=true` is set and an end-of-life build SDK emits
NETSDK1239.

### Story 1.4: Raise and pin test packages to their net6.0 ceilings

Satisfies CAP-4: xunit 2.9.2 to 2.9.3, Microsoft.NET.Test.Sdk 17.11.1 to 17.13.0,
xunit.runner.visualstudio 2.8.2 to 3.0.2. Pin all three so a later bump cannot silently
cross the ceiling — the table is in `stack.md`.

**Acceptance.** The three versions above are in place, `dotnet test CairnMultiplayerShared.Tests`
still passes, and all three ceilings are recorded where whoever bumps next will see them.

**Notes for the developer.** Verify with `dotnet test CairnMultiplayerShared.Tests` after the
bump, not with a successful restore. A clean restore proves nothing here: two of these three
packages restore fine past their ceiling and then fail at runtime with "Could not find
testhost". Do not exceed Test.Sdk 17.13.0 or runner 3.0.2.

**Sprint-planning assumption (not in the spec).** `CairnMultiplayerMod.Tests` carries the
same three pins as `CairnMultiplayerShared.Tests`. The spec's success criterion names only
the Shared suite, but leaving the Mod suite behind would reintroduce the drift the pins
exist to prevent — so bump and pin both, verifying only the Shared suite (the Mod suite
cannot run without game assemblies).
