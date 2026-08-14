---
title: 'Story 1.4: Raise and pin test packages to their net6.0 ceilings'
type: 'chore'
created: '2026-08-14'
status: 'done'
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

- `CairnMultiplayerShared.Tests/CairnMultiplayerShared.Tests.csproj:20,22,24` -- the three `PackageReference` lines to bump, each preceded by its ceiling comment. This suite is the only one CI can run, so it is where the change is actually proven.
- `CairnMultiplayerMod.Tests/CairnMultiplayerMod.Tests.csproj:20,22,24` -- byte-identical three lines. Bumped for parity; **cannot be verified in CI, or on any machine without `game-refs/`**, because its graph needs those gitignored assemblies. A developer who has them can exercise it locally -- see `scripts/check.sh:21`.
- `scripts/check.sh:21` -- `dotnet test CairnMultiplayer.slnx -c Release`, solution-wide, so it is the one path that does exercise the mod test project. Documented in `AGENTS.md:26` as the manual pre-push check. Not reachable from CI, which is why this story's mod-side edit still ships unexecuted here.
- `_bmad-output/specs/spec-cairnmp-melonloader-stack-currency/stack.md` -- the ceiling table and the AssetTargetFallback explanation. Source of truth for the numbers; do not restate its full reasoning in the csproj.
- `.github/workflows/ci.yml` -- runs `dotnet test CairnMultiplayerShared.Tests -c Release` on a .NET 6 runtime. This is the safety net story 1.1 built for exactly this change.
- `Directory.Build.props:46` -- `NoWarn=NETSDK1138` already suppresses the EOL-framework warning; expect no new build noise from the bump.

## Tasks & Acceptance

**Execution:**
- [x] `CairnMultiplayerShared.Tests/CairnMultiplayerShared.Tests.csproj` -- raise to `Microsoft.NET.Test.Sdk` 17.13.0, `xunit` 2.9.3, `xunit.runner.visualstudio` 3.0.2, each carrying a short comment naming it as the net6.0 ceiling and pointing at `stack.md` for why.
- [x] `CairnMultiplayerMod.Tests/CairnMultiplayerMod.Tests.csproj` -- apply the identical three versions and comments.
- [x] Try exact-version pins (`Version="[2.9.3]"` bracket syntax) so the resolved version cannot drift upward on its own. Keep them only if restore stays warning-free; if they produce NU16xx downgrade/conflict warnings, fall back to plain versions and say so — the comment carries the ceiling either way. **Kept.** Restore reported 0 warnings / 0 errors, and `project.assets.json` records the ranges as `[17.13.0, 17.13.0]`, `[2.9.3, 2.9.3]`, `[3.0.2, 3.0.2]`.
- [x] Verify locally: `dotnet test CairnMultiplayerShared.Tests -c Release` must pass, not merely restore. **63/63 passed, exit 0** on SDK 6.0.428 / runtime 6.0.36.
- [x] Verify on a runner: open a PR to `develop` and confirm the CI check is green on the bumped tree. **PR `yerayalfageme-glitch/cairnmp-mod#7`**; runs `31781260743` (`39e7e61`) and `31781342416` (`7b9d10a`) both green -- see Evidence below.

**Acceptance Criteria:**
- Given the bumped tree, when `dotnet test CairnMultiplayerShared.Tests -c Release` runs on a .NET 6 runtime, then all tests pass (63 at time of writing) and the run does not fail with "Could not find testhost".
- Given either test csproj, when a developer opens it to change a version, then the net6.0 ceiling for that package is visible on or beside that line.
- Given the two test projects, when their package versions are compared, then all three match exactly.
- Given the PR, when CI completes, then the check is green — the bump verified by execution rather than by restore.

## Spec Change Log

## Design Notes

**Why the recorded ceiling matters more than the numbers.** `xunit` 2.9.3 is the last release of the v2 line and the whole line is marked legacy on NuGet, so this bump buys no features — it buys being at a known, documented boundary. The next person to touch these versions is the one this story protects, and the only thing that protects them is a note where they are looking.

**Why the mod test project is bumped but not verified here.** Its graph needs game assemblies that cannot reach CI or a machine without `game-refs/`, so this edit ships unexecuted *by this story*. It is not unverifiable in principle: a developer holding those assemblies exercises it every time they run `scripts/check.sh`, which tests the whole solution. Shipping it unexecuted is accepted deliberately -- leaving it behind would reintroduce exactly the version drift the pins exist to prevent, and the three lines are identical to the ones that *are* verified.

**What the exact pins do and do not buy.** The bracket ranges stop *resolution* drifting upward -- no transitive dependency can quietly pull a higher `Microsoft.NET.Test.Sdk` past the ceiling. They stop nothing a human does deliberately: editing the number, or running `dotnet add package`, replaces the pin outright. Against that, the only guards are the comment sitting on the line and CI -- and CI covers the shared suite alone, so a mod-side mistake is caught by neither. The pins also carry an ongoing cost worth stating: a future package that genuinely requires a higher Test.Sdk will now hard-fail restore with NU1107/NU1608 instead of resolving upward. That failure is the pin working as designed, and unpinning is the sanctioned response to it -- after confirming the new ceiling in `stack.md`.

## Verification

**Commands:**
- `dotnet test CairnMultiplayerShared.Tests -c Release` -- expected: all tests pass, exit 0. A restore-only success is explicitly not acceptance.
- `gh pr checks <pr>` -- expected: the CI check green on the bumped tree.

**Manual checks:**
- Confirm the resolved versions are the intended ones (e.g. inspect `obj/project.assets.json` or the restore output), not merely that restore succeeded.

**Evidence (PR `yerayalfageme-glitch/cairnmp-mod#7` against `develop`):**
- The PR must be qualified by repo. `gh` resolves a bare `#7` against the `upstream` remote (`CairnMP/cairnmp-mod`), where PR 7 is an unrelated pull request.
- Green: run `31781260743` on commit `39e7e61` -- `Passed! - Failed: 0, Passed: 63, Skipped: 0, Total: 63`, 24s wall clock, no "Could not find testhost".
- Green: run `31781342416` on commit `7b9d10a` -- the spec-bookkeeping commit. 29s.
- Green: run `31782059279` on commit `63bddbc` -- the review-patch commit, which reworded the csproj comments; 63/63 again, confirming the comment edit did not break the XML. 20s.
- Every commit pushed to this branch gets its own run, because the workflow fires per push while the PR is open. So the PR *head* is verified, not merely its first commit -- and that stays true of any commit added after this line, which is why no sha is named as "the head" here. Current list: `gh run list --repo yerayalfageme-glitch/cairnmp-mod --branch chore/pin-test-packages-net6-ceilings`.
- Local, on SDK 6.0.428 / runtime 6.0.36: `dotnet test CairnMultiplayerShared.Tests -c Release` passed 63/63, exit 0. This machine had no .NET SDK at all beforehand, only the 6.0.36 runtime; the SDK was installed into a scratch directory for the run, so nothing about the repo or the machine's toolchain was altered to make it pass.
- Restore reported 0 warnings / 0 errors, so the bracket pins produced no NU16xx and were kept.
- **The strongest evidence, and the one the story turns on:** in `CairnMultiplayerShared.Tests/obj/project.assets.json`, `Microsoft.TestPlatform.TestHost/17.13.0` resolved its `lib/netcoreapp3.1/` assets, *not* `net462`. That is direct proof `AssetTargetFallback` never engaged -- the precise failure mode this story guards against, and the one a passing test run alone would not distinguish. The declared ranges are recorded there as `[17.13.0, 17.13.0]`, `[2.9.3, 2.9.3]`, `[3.0.2, 3.0.2]`.

**Not exercised:** `CairnMultiplayerMod.Tests`, for the reason given in the Code Map. Its three lines were compared against the verified ones and are identical, which is the whole of the assurance behind them.

## Suggested Review Order

**The durable deliverable -- what protects the next person**

- Entry point: the comment is the story's actual product; the versions are the one-off. Check it teaches the right thing.
  [`CairnMultiplayerShared.Tests.csproj:11`](../../CairnMultiplayerShared.Tests/CairnMultiplayerShared.Tests.csproj#L11)

- Exact-range brackets, kept only because restore stayed warning-free.
  [`CairnMultiplayerShared.Tests.csproj:20`](../../CairnMultiplayerShared.Tests/CairnMultiplayerShared.Tests.csproj#L20)

**The half that ships unexecuted**

- Byte-identical lines in the project no automated path can restore, build or run.
  [`CairnMultiplayerMod.Tests.csproj:20`](../../CairnMultiplayerMod.Tests/CairnMultiplayerMod.Tests.csproj#L20)

**Keeping the pointer honest**

- The comment sends readers here, so the "CairnMP has" column had to stop contradicting the tree.
  [`stack.md:24`](../specs/spec-cairnmp-melonloader-stack-currency/stack.md#L24)
