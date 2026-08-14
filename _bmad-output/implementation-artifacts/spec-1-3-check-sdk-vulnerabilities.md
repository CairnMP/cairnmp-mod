---
title: 'Story 1.3: Surface end-of-life build SDKs via CheckSdkVulnerabilities'
type: 'chore'
created: '2026-08-14'
status: 'in-progress'
baseline_commit: 'd0dc3eb0b61efa0a7f320c3c32d0f675a81bee9d'
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Nothing in the build reports that the SDK compiling this repo has gone out of support. CAP-3 asks for `CheckSdkVulnerabilities=true` so an end-of-life SDK emits NETSDK1239.

**Approach:** Set the property as specified, and state in the file what investigation established: it does nothing on any SDK shipping today. The property, its task, and codes NETSDK1238/1239/1240 exist only in dotnet/sdk `main` (.NET 11, still preview). Setting it now is a forward-looking tripwire that arms itself when the toolchain reaches .NET 11 — not active protection. Recording that is the point; an undocumented inert flag is worse than no flag, because it reads as a guard that is watching.

## Boundaries & Constraints

**Always:** Say plainly, in the file itself, that the property is inert until .NET 11 — anyone who finds it must not mistake it for live cover.

**Ask First:** Adding `dotnet sdk check` to CI or any script. Removing the `NETSDK1138` suppression. Adding `global.json`.

**Never:** Change `<TargetFramework>`. Touch `.github/workflows/ci.yml`. Claim CAP-3's second clause is satisfied — no shipping SDK can emit NETSDK1239, so that clause is recorded unmet rather than asserted.

</frozen-after-approval>

## Code Map

- `Directory.Build.props:39-47` -- the shared style/analysis `PropertyGroup`, applied to all four projects. Both edits land here.
- `Directory.Build.props:46` -- `<NoWarn>NETSDK1138</NoWarn>`, written in overwrite form. Convention is `$(NoWarn);NETSDK1138`; as written it discards anything an SDK or earlier props file set.
- `.github/workflows/ci.yml` -- installs the .NET 6 SDK and runs the shared suite. Read-only here; it is the check that the edit breaks nothing.

## Tasks & Acceptance

**Execution:**
- [x] `Directory.Build.props` -- add `<CheckSdkVulnerabilities>true</CheckSdkVulnerabilities>` with a comment recording: what it guards (the build SDK, not the target framework, so it will not fire on the `net6.0` pin), that it is inert until .NET 11 GA, and that even then the task reads a local cache and silently no-ops when that cache is absent. **Done**, `Directory.Build.props:48-62`. The comment also names `dotnet sdk check` as the thing that works today and why it was not adopted, so the next reader does not re-derive that.
- [x] `Directory.Build.props:46` -- change `NoWarn` to the append form `$(NoWarn);NETSDK1138`, preserving anything set upstream. **Done**, now `Directory.Build.props:69`.
- [x] Keep the `NETSDK1138` suppression itself. Investigation could not show it is redundant: with `NoWarn` cleared, both a library and the `OutputType=Exe` test project built with 0 warnings on SDK 8.0.424, but CI builds on .NET 10, whose EOL list is expected to include `net6.0`. Absence of the warning on one SDK is not evidence about another. **Kept**, with a comment naming it as the target-framework counterpart to the property above it.
- [x] Verify the build is unchanged locally: build both shared projects, expecting no new diagnostics. **0 warnings / 0 errors** on both, including a `--no-incremental` rebuild, and again under the .NET 6 SDK CI actually uses -- see Evidence.
- [x] Verify on a runner: open a PR to `develop` and confirm the CI check stays green. **PR `yerayalfageme-glitch/cairnmp-mod#8`** -- see Evidence.

**Acceptance Criteria:**
- Given the edited props file, when any of the four projects builds, then no new warning or error appears that was not there before.
- Given a developer reading `Directory.Build.props`, when they find `CheckSdkVulnerabilities`, then the file tells them it is inert until .NET 11 rather than implying live protection.
- Given an SDK or props file that set `NoWarn` before this file is evaluated, when this file is applied, then those codes survive instead of being discarded.
- Given CAP-3's second clause (an EOL SDK emits NETSDK1239), when acceptance is assessed, then it is recorded as unmet-by-toolchain rather than claimed.

## Spec Change Log

## Design Notes

**Why an inert property is still worth setting.** It costs one line, it is correct as written, and it arms itself the moment the toolchain moves — there is no later migration to remember. The risk is purely that someone reads it as active cover, which the comment removes. The alternative that works *today* is `dotnet sdk check`, deliberately not adopted here: it is a CI step rather than a build property, and on this repo's runner it would flag the .NET 6 SDK the workflow installs on purpose, producing a permanent known-noise signal.

**Why the check can never catch a currently-EOL SDK.** It ships forward only. An SDK old enough to be out of support predates the feature and cannot warn about itself; an SDK new enough to implement it will not be EOL for years. This is a tripwire for a future transition, not a detector of the present state — worth stating because the capability's wording suggests otherwise.

## Verification

**Commands:**
- `dotnet build CairnMultiplayerShared -c Release` and `dotnet build CairnMultiplayerShared.Tests -c Release` -- expected: 0 warnings, unchanged from before the edit.
- `dotnet test CairnMultiplayerShared.Tests -c Release` -- expected: all tests pass, exit 0.

**Manual checks:**
- Confirm the property is actually read as `true` (e.g. `dotnet msbuild -getProperty:CheckSdkVulnerabilities`), so the story does not ship a typo that no diagnostic would ever reveal — the failure mode of an inert flag is total silence either way.
