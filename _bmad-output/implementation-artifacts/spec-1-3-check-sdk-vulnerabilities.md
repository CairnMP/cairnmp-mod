---
title: 'Story 1.3: Surface end-of-life build SDKs via CheckSdkVulnerabilities'
type: 'chore'
created: '2026-08-14'
status: 'review'
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

*Line numbers in this section are **pre-edit** (as the story was planned). The Tasks, Evidence and Review Order sections below use **post-edit** numbers, because they describe the shipped file.*

- `Directory.Build.props:39-47` -- the shared style/analysis `PropertyGroup`, applied to all four projects. Both edits land here.
- `Directory.Build.props:46` -- `<NoWarn>NETSDK1138</NoWarn>`, written in overwrite form. Convention is `$(NoWarn);NETSDK1138`; as written it discards anything an SDK or earlier props file set.
- `.github/workflows/ci.yml` -- installs the .NET 6 SDK and runs the shared suite. Read-only here; it is the check that the edit breaks nothing. Note it installs 6.0.428 but does not *select* it: there is no `global.json`, so MSBuild takes the highest SDK present on the runner. The 6.0.x install is what supplies the runtime the testhost needs.

## Tasks & Acceptance

**Execution:**
- [x] `Directory.Build.props` -- add `<CheckSdkVulnerabilities>true</CheckSdkVulnerabilities>` with a comment recording: what it guards (the build SDK, not the target framework, so it will not fire on the `net6.0` pin), that it is inert until .NET 11 GA, and that even then the task reads a local cache and silently no-ops when that cache is absent. **Done**, `Directory.Build.props:47-74` (comment 47-73, property 74). The comment also cites the Microsoft Learn NETSDK1239 page, says what NETSDK1238 and NETSDK1240 cover so the reader can tell the three apart, explains why a property named "…Vulnerabilities" is being set for an end-of-support signal, and names `dotnet sdk check` as the thing that works today and why it was not adopted.
- [x] `Directory.Build.props:46` (pre-edit) -- change `NoWarn` to the append form `$(NoWarn);NETSDK1138`, preserving anything set upstream. **Done**, now `Directory.Build.props:85`.
- [x] Keep the `NETSDK1138` suppression itself. Investigation could not show it is redundant: with `NoWarn` cleared, both a library and the `OutputType=Exe` test project built with 0 warnings on SDK 8.0.424, but CI's building SDK is a different and newer one, whose EOL list is expected to include `net6.0`. Absence of the warning on one SDK is not evidence about another. **Kept**, with a comment naming it as the target-framework counterpart to the property above it. The case for keeping it got *stronger* on investigation: which SDK builds on CI is undetermined (see Evidence), so there is no SDK on which the suppression has been shown redundant, and this very suppression is what would hide the evidence either way.
- [x] Verify the build is unchanged locally: build both shared projects, expecting no new diagnostics. **0 warnings / 0 errors** on both, including a `--no-incremental` rebuild, and again under SDK 6.0.428 -- the version CI *installs*, though not necessarily the one it builds with. See Evidence.
- [x] Verify on a runner: open a PR to `develop` and confirm the CI check stays green. **PR `yerayalfageme-glitch/cairnmp-mod#8`** -- see Evidence.

**Acceptance Criteria:**
- Given the edited props file, when any of the four projects builds, then no new warning or error appears that was not there before.
- Given a developer reading `Directory.Build.props`, when they find `CheckSdkVulnerabilities`, then the file tells them it is inert until .NET 11 rather than implying live protection.
- Given an SDK or props file that set `NoWarn` before this file is evaluated, when this file is applied, then those codes survive instead of being discarded.
- Given CAP-3's second clause (an EOL SDK emits NETSDK1239), when acceptance is assessed, then it is recorded as unmet-by-toolchain rather than claimed.

## Spec Change Log

## Design Notes

**Why an inert property is still worth setting.** It costs one line, it is correct as written, and it arms itself the moment the toolchain moves. The risk is not *purely* that someone reads it as active cover, which the comment removes. The property exists only in a preview branch, so it can still be renamed, re-scoped or dropped before .NET 11 GA — and if that happens the line becomes dead text that goes on reading as armed, which is the exact failure the comment exists to prevent. So this is not quite "set it and forget it": the line needs re-checking when the toolchain reaches .NET 11, and the comment says so. The alternative that works *today* is `dotnet sdk check`, deliberately not adopted here: it is a CI step rather than a build property, and on this repo's runner it would flag the .NET 6 SDK the workflow installs on purpose, producing a permanent known-noise signal.

**Why the check can never catch a currently-EOL SDK.** It ships forward only. An SDK old enough to be out of support predates the feature and cannot warn about itself; an SDK new enough to implement it will not be EOL for years. This is a tripwire for a future transition, not a detector of the present state — worth stating because the capability's wording suggests otherwise.

## Verification

**Commands:**
- `dotnet build CairnMultiplayerShared -c Release` and `dotnet build CairnMultiplayerShared.Tests -c Release` -- expected: 0 warnings, unchanged from before the edit.
- `dotnet test CairnMultiplayerShared.Tests -c Release` -- expected: all tests pass, exit 0.

**Manual checks:**
- Confirm the property is actually read as `true` (e.g. `dotnet msbuild -getProperty:CheckSdkVulnerabilities`), so the story does not ship a typo that no diagnostic would ever reveal — the failure mode of an inert flag is total silence either way.

**Evidence:**
- **The check that matters most for an inert flag**, run on **SDK 8.0.424** (static evaluation, so the SDK version does not affect the answer — but it is neither the SDK CI installs nor the one CI builds with, so it is named rather than left ambiguous). `dotnet msbuild CairnMultiplayerShared.csproj -getProperty:CheckSdkVulnerabilities -getProperty:NoWarn -getProperty:TargetFramework` returned `{"CheckSdkVulnerabilities": "true", "NoWarn": ";NETSDK1138", "TargetFramework": "net6.0"}`. The property is genuinely evaluated as `true` on the real project, not merely typed into the file — the only way to distinguish a working inert flag from a misspelled one, since neither emits anything.
- **Why "inert until .NET 11" is a finding and not an assumption.** `CheckSdkVulnerabilities` is documented at [learn.microsoft.com/dotnet/core/tools/sdk-errors/netsdk1239](https://learn.microsoft.com/dotnet/core/tools/sdk-errors/netsdk1239), which carries no version moniker — so the docs alone do not tell you it is unshipped, and a reader could reasonably conclude the opposite. Two things settle it: the implementation lives in dotnet/sdk `main` only (`src/Tasks/Microsoft.NET.Build.Tasks/CheckSdkVulnerabilities.cs`, `src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.SdkVulnerabilityCheck.targets`, `src/Tasks/Common/Resources/Strings.resx`), first available in `11.0.100-preview.5`; and searching both locally installed SDKs, **6.0.428 and 8.0.424, found no `CheckSdkVulnerabilities` string and no `SdkVulnerability*` file in either**. That search is the direct evidence — the docs page is not.
- **A local build proving nothing, recorded so it is not mistaken for proof.** The clean 8.0.424 build does *not* show the property is inert: .NET 8 is in support until November 2026, so NETSDK1239 would stay silent on 8.0.424 even if the check were fully implemented there. Silence is the expected output in both worlds; only the file search distinguishes them.
- **`NoWarn` really does preserve upstream codes**, tested rather than assumed: a scratch project that sets `NoWarn=CS1591;UPSTREAM0001` and then imports this props file evaluates to `CS1591;UPSTREAM0001;NETSDK1138`. Before the change it would have evaluated to `NETSDK1138` alone. Note the leading `;` in the project-level value above: upstream is empty there, and empty entries are ignored by both MSBuild and the compiler — 0 warnings confirms it.
- Local builds, SDK 8.0.424 / runtime 6.0.36: `dotnet build CairnMultiplayerShared -c Release` and `dotnet build CairnMultiplayerShared.Tests -c Release` both `0 Warning(s), 0 Error(s)`, including a `--no-incremental` rebuild so the result is not an up-to-date check reporting an old success.
- Local rebuild under **SDK 6.0.428**, `-t:Rebuild -p:Configuration=Release`: clean, exit 0. Both SDKs were installed into a scratch directory; this machine has no system SDK, only the 6.0.36 runtime, so nothing about the repo or the machine's toolchain was altered to make the build pass.
- **What that 6.0.428 rebuild does and does not reproduce.** It reproduces the SDK CI *installs*, not necessarily the SDK CI *builds with*. The run log shows `setup-dotnet` fetching runtime 10.0.11 and then SDK 6.0.428 into the shared `/usr/share/dotnet`, but there is no `global.json` in this repo, so MSBuild selects the **highest** SDK present — and the ubuntu runner image preinstalls 10.x (`ci.yml` says as much). The 6.0.x install is there to supply the runtime the `net6.0` testhost needs, which is a separate job from selecting the build SDK. **Which SDK compiled the code is therefore undetermined, and most likely 10.x.** The logs cannot settle it: they never print the selected SDK, and the one diagnostic that would have hinted at it (NETSDK1138 on a `net6.0` target) is suppressed by this very props file either way. Not asserted, because it cannot be. This is also why the NETSDK1138 suppression stays: there is no SDK on which it has been shown redundant.
- `dotnet test CairnMultiplayerShared.Tests -c Release`: 63/63 passed, exit 0.
- **On a runner** — PR `yerayalfageme-glitch/cairnmp-mod#8` against `develop`, run `31784180366` on commit `7ad9b9f`, green in 22s: `Passed! - Failed: 0, Passed: 63, Skipped: 0, Total: 63`. The full log contains no `NETSDK` diagnostic and no build warning, so the edit introduced no new noise on the runner either. The PR must be qualified by repo — a bare `#8` resolves against `upstream` (`CairnMP/cairnmp-mod`), a different pull request.
- Green again on run `31784333992` (commit `5b43468`, the spec-bookkeeping commit), 21s. Every push to this branch gets its own run while the PR is open, so the *head* is verified rather than only the first commit — and that stays true of any commit added after this line, which is why no sha is named as "the head" here. Current list: `gh run list --repo yerayalfageme-glitch/cairnmp-mod --branch chore/check-sdk-vulnerabilities`.
- **CAP-3 second clause: unmet-by-toolchain, not satisfied.** No SDK that ships today can emit NETSDK1239, so nothing in this change makes an EOL SDK warn. It is recorded here as unmet rather than claimed, per the Intent above.

**Not exercised:** the property firing. It cannot be, by anyone, until a .NET 11 SDK exists — that is the whole point of the story and the reason the comment in the file is the deliverable rather than the property.

## Suggested Review Order

*Post-edit line numbers.*

**The actual deliverable — the comment, not the property**

- Entry point. Two lines of build config carry about 36 lines of comment between them, and that ratio is the story: judge whether a developer who finds this in two years can tell it is not watching anything. The property itself is unreviewable — it has no observable behaviour on any SDK that exists.
  [`Directory.Build.props:47`](../../Directory.Build.props#L47)

**The change that does something today**

- `NoWarn` in append form. This is the only edit with a behavioural difference on a current SDK: codes set upstream now survive instead of being discarded. Its comment also carries why the NETSDK1138 suppression stays.
  [`Directory.Build.props:85`](../../Directory.Build.props#L85)

**Scope note**

- The `PropertyGroup` header was translated to English per `AGENTS.md:30`, and reworded from "code style" to name what the group now actually holds — a support-policy tripwire and a diagnostic suppression are not code style, so a literal translation would have left the one translated line the one inaccurate line. The French comments above line 39 were left alone: translating them means restating the "versionné" claim about `game-refs/` that `deferred-work.md` already records as false, which is a CAP-2 decision rather than one to make in passing here.
  [`Directory.Build.props:40`](../../Directory.Build.props#L40)
