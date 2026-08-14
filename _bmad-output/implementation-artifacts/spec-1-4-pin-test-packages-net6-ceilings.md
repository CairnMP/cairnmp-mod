---
title: 'Story 1.4: Raise and pin test packages to their net6.0 ceilings'
type: 'chore'
created: '2026-08-14'
status: 'in-progress'
baseline_commit: 'aad1f83ab661addf1e73f99feebfe28f0aee8685'
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The three test packages sit below the highest versions the `net6.0` pin allows, and nothing records where those ceilings are. Two of the three do not fail loudly when crossed: `Microsoft.NET.Test.Sdk` above 17.13.0 and `xunit.runner.visualstudio` above 3.0.2 restore cleanly via `AssetTargetFallback` and then fail at *runtime* with "Could not find testhost" — an error that reads as a broken test setup rather than an unsupported package. So the next person to bump gets no signal at the moment they make the mistake.

**Approach:** Raise all three to their ceilings, and make the ceiling visible at the exact place someone would edit the version. The version bump is a one-off; the recorded ceiling is the durable deliverable.

## Boundaries & Constraints

**Always:** Verify by actually running the shared suite, never by a clean restore — a clean restore is precisely the failure mode's disguise. Apply the same three versions to both test projects, so they cannot drift apart.

**Ask First:** Adopting Central Package Management (`Directory.Packages.props`) as the pinning mechanism — it would centralise versions for every project, not just these two. Changing `Directory.Build.props`. Raising anything beyond a ceiling.

**Never:** Exceed `Microsoft.NET.Test.Sdk` 17.13.0, `xunit.runner.visualstudio` 3.0.2, or the `xunit` v2 line. Migrate to xunit v3 (it floors at net8.0). Change `<TargetFramework>`. Touch the CI workflow — it is the verifier for this change, not part of it.

</frozen-after-approval>

## Code Map

- `CairnMultiplayerShared.Tests/CairnMultiplayerShared.Tests.csproj:11-13` -- the three `PackageReference` lines to bump. This suite is the only one CI can run, so it is where the change is actually proven.
- `CairnMultiplayerMod.Tests/CairnMultiplayerMod.Tests.csproj:11-13` -- byte-identical three lines. Bumped for parity; **cannot be built or verified anywhere** because its graph needs the gitignored `game-refs/`.
- `_bmad-output/specs/spec-cairnmp-melonloader-stack-currency/stack.md` -- the ceiling table and the AssetTargetFallback explanation. Source of truth for the numbers; do not restate its full reasoning in the csproj.
- `.github/workflows/ci.yml` -- runs `dotnet test CairnMultiplayerShared.Tests -c Release` on a .NET 6 runtime. This is the safety net story 1.1 built for exactly this change.
- `Directory.Build.props:46` -- `NoWarn=NETSDK1138` already suppresses the EOL-framework warning; expect no new build noise from the bump.

## Tasks & Acceptance

**Execution:**
- [x] `CairnMultiplayerShared.Tests/CairnMultiplayerShared.Tests.csproj` -- raise to `Microsoft.NET.Test.Sdk` 17.13.0, `xunit` 2.9.3, `xunit.runner.visualstudio` 3.0.2, each carrying a short comment naming it as the net6.0 ceiling and pointing at `stack.md` for why.
- [x] `CairnMultiplayerMod.Tests/CairnMultiplayerMod.Tests.csproj` -- apply the identical three versions and comments.
- [x] Try exact-version pins (`Version="[2.9.3]"` bracket syntax) so the resolved version cannot drift upward on its own. Keep them only if restore stays warning-free; if they produce NU16xx downgrade/conflict warnings, fall back to plain versions and say so — the comment carries the ceiling either way. **Kept.** Restore reported 0 warnings / 0 errors, and `project.assets.json` records the ranges as `[17.13.0, 17.13.0]`, `[2.9.3, 2.9.3]`, `[3.0.2, 3.0.2]`.
- [x] Verify locally: `dotnet test CairnMultiplayerShared.Tests -c Release` must pass, not merely restore. **63/63 passed, exit 0** on SDK 6.0.428 / runtime 6.0.36.
- [ ] Verify on a runner: open a PR to `develop` and confirm the CI check is green on the bumped tree.

**Acceptance Criteria:**
- Given the bumped tree, when `dotnet test CairnMultiplayerShared.Tests -c Release` runs on a .NET 6 runtime, then all tests pass (63 at time of writing) and the run does not fail with "Could not find testhost".
- Given either test csproj, when a developer opens it to change a version, then the net6.0 ceiling for that package is visible on or beside that line.
- Given the two test projects, when their package versions are compared, then all three match exactly.
- Given the PR, when CI completes, then the check is green — the bump verified by execution rather than by restore.

## Spec Change Log

## Design Notes

**Why the recorded ceiling matters more than the numbers.** `xunit` 2.9.3 is the last release of the v2 line and the whole line is marked legacy on NuGet, so this bump buys no features — it buys being at a known, documented boundary. The next person to touch these versions is the one this story protects, and the only thing that protects them is a note where they are looking.

**Why the mod test project is bumped but not verified.** Its graph needs game assemblies that cannot reach CI or a clean machine, so this edit ships unexecuted. That is accepted deliberately: leaving it behind would reintroduce exactly the version drift the pins exist to prevent, and the three lines are identical to the ones that *are* verified.

## Verification

**Commands:**
- `dotnet test CairnMultiplayerShared.Tests -c Release` -- expected: all tests pass, exit 0. A restore-only success is explicitly not acceptance.
- `gh pr checks <pr>` -- expected: the CI check green on the bumped tree.

**Manual checks:**
- Confirm the resolved versions are the intended ones (e.g. inspect `obj/project.assets.json` or the restore output), not merely that restore succeeded.
