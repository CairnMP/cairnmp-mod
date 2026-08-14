# Deferred Work

Append-only ledger. Entries are added by build reviews and triaged later; do not
edit or de-duplicate existing entries.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-1-protocol-test-suite-ci.md`
  summary: The new CI check is advisory — neither `develop` nor `production` has branch protection or a ruleset, so a red run does not block a merge.
  evidence: `gh api repos/yerayalfageme-glitch/cairnmp-mod/branches/develop/protection` returns 404 "Branch not protected" and `.../rulesets` returns `[]`. Story 1.1 proves CI *can* turn red; nothing yet makes a red run stop anything, which is the gap between "verified" and "observed". Two independent review layers raised this. Fixing it is a repo-settings change, not a code change.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-1-protocol-test-suite-ci.md`
  summary: A branch pushed without an open PR gets no verification, which is narrower than the epic's stated bar of "every push".
  evidence: `push` is filtered to the long-lived branches so an internal PR cannot fire twice for one SHA — a deliberate trade-off, and the acceptance criterion for exactly-one-run depends on it. But `epic-1-context.md` and the spec Intent both say "every push", and a topic branch with no PR runs nothing. In practice all work reaches `develop` via PR, so coverage is complete along the normal path; the wording and the triggers still disagree, and which one gives way is a human call.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-1-protocol-test-suite-ci.md`
  summary: Contributor docs still point at the solution-wide `scripts/check.sh` and never mention that CI now exists with a deliberately narrower scope.
  evidence: `AGENTS.md:22-26` presents `bash scripts/check.sh` as the pre-push verification, and `scripts/check.sh:21` runs `dotnet test CairnMultiplayer.slnx` — solution-wide, so it needs the gitignored `game-refs/` and cannot succeed without a local game install. `README.md` has no CI badge and no Actions mention. The local gate and the CI gate now differ in scope with nothing explaining the difference. Pre-existing for `check.sh`; newly confusing now that CI exists.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-1-protocol-test-suite-ci.md`
  summary: Workflow actions are referenced by mutable major tags with no SHA pinning and no `dependabot.yml` for the `github-actions` ecosystem.
  evidence: `actions/checkout@v7` and `actions/setup-dotnet@v6` resolve to whatever those tags point at on the day of the run. For an epic whose premise is security posture on an unpatched runtime, an unpinned third-party action in the verification path is a loose thread — though pinning trades supply-chain tightness for manual upgrade toil, so it is a judgment call rather than an obvious fix.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-1-protocol-test-suite-ci.md`
  summary: A push to `develop` while a `develop` → `production` PR is open produces two runs for the same SHA.
  evidence: The push and pull_request events both match, and the concurrency group is keyed on `github.ref`, which differs between the two event contexts, so neither cancels the other. Narrow corner — it needs a release-shaped PR between two long-lived branches — and it costs duplicate minutes rather than correctness, but it is the one case where the exactly-one-run criterion does not hold.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-1-protocol-test-suite-ci.md`
  summary: Test results exist only as console scrollback — no `.trx` logger, no uploaded artifact, no job summary.
  evidence: The acceptance criterion "the job fails and names that test" is currently satisfied by raw log text alone. That held for the red proof, but a test-host launch failure or a cancelled run leaves nothing durable to triage after logs age out.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-2-generate-refs-no-commit-instruction.md`
  summary: `scripts/package-mod.ps1` prints "(versioned)" for two `game-refs/` paths — the same false claim story 1.2 removed from the sibling script.
  evidence: `package-mod.ps1:27` and `:56` describe `game-refs/` and `game-refs/MelonLoader/` as "versioned", but `.gitignore:2` excludes that directory. CAP-2's scope named only `generate-il2cpp-refs.ps1`, so this was out of story scope — but CAP-2's success bar in SPEC.md is the durable property "nothing left in the repo instructs a contributor to commit `game-refs/`", which this still violates in spirit. `package-mod.ps1` is also the script the fixed one hands off to.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-2-generate-refs-no-commit-instruction.md`
  summary: `Directory.Build.props` twice describes `game-refs/` as "versionné" — same contradiction, in the file that is the authority on where references resolve from.
  evidence: Lines 16 and 21 carry the claim, in French. It is what a reader consults when the script output confuses them, so the wrong claim survives where it is most load-bearing. Story 1.1's spec listed `Directory.Build.props` as read-only, hence deferred rather than fixed in passing.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-2-generate-refs-no-commit-instruction.md`
  summary: Nothing guards CAP-2 against regression — the corrected message is protected only by convention.
  evidence: CAP-2's bar is a durable repo property, but `.github/workflows/ci.yml` runs only the shared test suite and `scripts/check.sh` only the solution test. A grep-style assertion would fit the CI constraint exactly (it needs no game assemblies and no secrets) and would stop the instruction reappearing on the next edit of either script.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-2-generate-refs-no-commit-instruction.md`
  summary: "You can now run package-mod.ps1" can dead-end a first-time contributor, because that script also needs MelonLoader DLLs this one never produces.
  evidence: `package-mod.ps1:49-53` requires `game-refs/MelonLoader/{MelonLoader,Il2CppInterop.Runtime,0Harmony}.dll` and throws when they are absent; `generate-il2cpp-refs.ps1` only produces the Il2Cpp assemblies. The wording predates story 1.2 (the original line said the same), so it was left alone — but the handoff is incomplete and the failure lands after a five-minute game launch.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-2-generate-refs-no-commit-instruction.md`
  summary: `README.md` documents two ways to supply reference assemblies and never mentions the script that generates them.
  evidence: `README.md:33-38` lists the manual drop-in and `MELON_LOADER_NET6_DIR`; `scripts/generate-il2cpp-refs.ps1` appears nowhere outside an error branch in `package-mod.ps1`. Contributors are unlikely to find the corrected guidance because they are unlikely to find the script.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-4-pin-test-packages-net6-ceilings.md`
  summary: Nothing mechanically enforces that the two test projects carry identical package versions — the acceptance criterion is satisfiable only by a human comparing two files.
  evidence: The pins exist to stop version drift, but the parity between `CairnMultiplayerShared.Tests` and `CairnMultiplayerMod.Tests` is preserved only by whoever edits next remembering the other file — the same human-memory mechanism the pins were introduced to replace. A byte-comparison of the two version blocks needs no SDK and no game assemblies, so it would fit CI's constraints exactly; Central Package Management would also solve it but centralises versions for every project. Out of scope here because this story's boundaries forbid touching the CI workflow.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-4-pin-test-packages-net6-ceilings.md`
  summary: The CI check observes only the process exit code, so a partial collapse in test discovery would stay invisible behind a green check.
  evidence: `ci.yml:51` runs a bare `dotnet test` with no result-file inspection and no minimum-count assertion. If a future runner or xunit change left the adapter discovering a subset of the suite, every discovered test would still pass, `dotnet test` would still exit 0, and the check would be green with a fraction of the protocol surface actually run — the repo's own research names silent discovery failure as the characteristic risk of a mismatched runner. Total discovery failure does surface as an error; the gap is the partial case. A floor on the reported `Passed:` count is roughly one line in the existing step.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-4-pin-test-packages-net6-ceilings.md`
  summary: The `CairnMultiplayerMod.Tests` pins ship unexecuted — no automated path restores, builds or runs that project.
  evidence: CI deliberately runs only the shared suite, and `scripts/check.sh` (the one command that would exercise the mod tests) is manual-only and needs the gitignored `game-refs/`. A ceiling breach or a plain typo in that file — `[17.14.0]`, or `[2.9.13]` which would not even resolve — leaves CI green and surfaces later on a contributor's machine as the "reads like a broken test setup" error this story exists to prevent. Accepted knowingly when the story was approved; filed here so triage sees it rather than only the story file.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-4-pin-test-packages-net6-ceilings.md`
  summary: `sprint-status.yaml` is being edited on three branches in parallel and will conflict when their PRs land.
  evidence: Story 1.1 is `done` on `fix/generate-refs-no-commit-instruction`, still `review` on `develop`, and story 1.4 moved to `review` on its own branch. Each story branch edits the same few lines of the same file, so PRs #6 and #7 both touch it against a `develop` that has since moved. Not a defect in any one story — a consequence of running stories on parallel branches with a single shared board file. Worth deciding whether the board should be updated only on `develop` after merge, or the conflicts simply resolved by hand each time.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-3-check-sdk-vulnerabilities.md`
  summary: Nothing asserts that `CheckSdkVulnerabilities` is still present and correctly spelled, and a typo in it is undetectable by any means the repo has.
  evidence: The property emits nothing on any shipping SDK, so misspelling it, or dropping it during a future edit to that `PropertyGroup`, leaves the build byte-identical and CI green. Worse, the silence persists after .NET 11 arrives: an absent NETSDK1239 would read as "SDK supported" rather than "property misspelled". The story's own manual `dotnet msbuild -getProperty:CheckSdkVulnerabilities` probe is the check that would catch it; made repeatable in `scripts/check.sh` or the CI job it would close the gap. Out of scope here because the story's boundaries forbid touching either.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-3-check-sdk-vulnerabilities.md`
  summary: `WarningsAsErrors` in `Directory.Build.props` is still in the overwrite form the story just fixed one line below in `NoWarn`.
  evidence: `<WarningsAsErrors>CS8600;CS8601;CS8602;CS8603;CS8604</WarningsAsErrors>` discards anything an outer props file set, the same defect and the same `PropertyGroup` as the `NoWarn` line. Left alone because the approved scope named the `NoWarn` issues specifically, and because a curated error-promotion list is more plausibly deliberate than a curated suppression list — worth a decision rather than a reflex fix.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-3-check-sdk-vulnerabilities.md`
  summary: The new `NoWarn` append form lets an upstream suppression silently cancel the null-flow error promotion.
  evidence: `NoWarn` takes precedence over `WarningsAsErrors`, so an outer props file setting e.g. `NoWarn=CS8602` would now defeat the CS8600-CS8604 promotion that `Directory.Build.props` relies on; under the old overwrite form it was discarded. No such upstream setter exists in this repo today, so the impact is currently zero — but nothing asserts those five codes are still error-promoted, so the loss would be silent if one ever appeared.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-3-check-sdk-vulnerabilities.md`
  summary: `CheckSdkVulnerabilities` needs re-checking when the toolchain reaches .NET 11 GA, because it is currently specified only by an unshipped preview branch.
  evidence: The property, its task and NETSDK1238/1239/1240 exist only in dotnet/sdk `main`. Anything unshipped can be renamed, re-scoped or dropped before GA — and if it is, the line becomes dead text that still reads as an armed tripwire, which is the precise failure the comment was written to prevent. Nothing currently schedules that look.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-3-check-sdk-vulnerabilities.md`
  summary: `Directory.Build.props` now ships permanently bilingual, with only the touched block translated.
  evidence: `AGENTS.md:30` asks that French files be translated when touched. Only the edited `PropertyGroup` header was, because translating the file header means restating its claim that `game-refs/` is "versionné" — which `deferred-work.md` already records as false and which belongs to a CAP-2 decision, not to a passing edit. The half-translated state is deliberate but unmarked, and the two are best resolved together.

- source_spec: `_bmad-output/implementation-artifacts/epic-1-retro-2026-08-14.md`
  summary: CAP-2's own bar, grepped literally, now matches the records that describe the defect — so the proposed grep-based regression guard would fail on the retrospective that proposed it.
  evidence: After the promotion (PR #13, `2c71a2f`), `git grep -i "commit these files" origin/production` returns four hits, all inside `_bmad-output/`: the retrospective quoting the old script line, `spec-1-2` stating the problem it fixed, and the research report that found it. Excluding `_bmad-output/*` returns nothing, so CAP-2 is genuinely met on both branches — but the phrase now lives on the release branch permanently, in historical quotation. Two consequences. The regression guard this ledger already asks for (grep-style, no game assemblies, fits the CI constraint) must scope itself to instructing files or it fires on its own evidence. And action item 8's rewording of CAP-2 should say the bar binds files that *instruct*, not every file that contains the string, or the reworded clause inherits the same flaw. Neither is visible from `develop` alone, which is why no story review could have caught it.

- source_spec: `_bmad-output/implementation-artifacts/epic-1-retro-2026-08-14.md`
  summary: The duplicate-run entry in this ledger describes a narrower trigger than the one that actually fires; PR #13 produced the duplicate without the push it names.
  evidence: The existing entry says a push to `develop` *while* a `develop` → `production` PR is open yields two runs for one SHA. Observed on PR #13: the push run (`31792852280`, from #12's merge) had already completed when the promotion PR was opened, and opening it added a `pull_request` run (`31792960792`) against the same SHA — two runs, no concurrent push. The diagnosis in the original entry is right (the concurrency group keys on `github.ref`, which differs between event contexts, so neither cancels the other), but its precondition is not needed: any SHA already pushed to a trigger branch collects a second run the moment a PR is opened from it. Cost is duplicate minutes, not correctness. Worth folding into the original entry at triage rather than fixing separately.

- source_spec: `_bmad-output/implementation-artifacts/spec-epic-1-record-corrections.md`
  summary: The amended repo-wide Success signal in `SPEC.md` is stated unmet: four live instructing lines still call `game-refs/` versioned, and this entry exists so triage sees the bar and its gap in one place.
  evidence: The Success signal was restated (action item 8, `decision-q3-name-branches-in-spec`) to bind files that *instruct* a contributor or agent on `develop`, `production` and `next/feature-framework`, excluding historical records under `_bmad-output/` — the exclusion being what stops the bar from being satisfied by the retrospective, `spec-1-2` and the research report, which quote the old script message in order to document it. Under the amended wording the bar is false today, and was deliberately not softened to make it pass. The four lines are `scripts/package-mod.ps1:27,56` ("versioned") and `Directory.Build.props:16,21` ("versionné"), each already carried by an existing entry above from `spec-1-2-generate-refs-no-commit-instruction.md` — the `package-mod.ps1` "(versioned)" entry and the `Directory.Build.props` "versionné" entry. Those two are the work; this one is the bar that now names them as a gap rather than leaving the property implicit. Fixing them is not a mechanical string swap: `Directory.Build.props` is French and `AGENTS.md:30` asks that French files be translated when touched, which is the same knot the "permanently bilingual" entry above records — resolve all three together. Belongs to action item 6 (ledger triage); the records correction that raised it deliberately changed no product file.

- source_spec: `_bmad-output/implementation-artifacts/spec-epic-1-record-corrections.md`
  summary: The first entry in this ledger is now known false — it says neither `develop` nor `production` is protected, but `production` is, and the append-only rule means the correction has to be an append like this one.
  evidence: The entry states `gh api .../branches/develop/protection` returns 404 and `.../rulesets` returns `[]`, concluding a red run blocks nothing. `decision-q1-production-in-scope` in `sprint-status.yaml` records the opposite for the release branch: `production` carries a required status check (`Protocol test suite`), `enforce_admins: true` and `required_conversation_resolution: true`, and the decision notes explicitly say this supersedes the ledger entry. The retrospective independently observed an admin push to `develop` rejected with `GH006 … Required status check "Protocol test suite" is expected`, so `develop` is protected too. Triage must not read the original entry as current: the CI check is advisory on neither long-lived branch. Whether the entry's underlying ask (make red runs blocking everywhere, including `next/feature-framework`) is fully discharged is the open part.

- source_spec: `_bmad-output/implementation-artifacts/spec-epic-1-record-corrections.md`
  summary: `SPEC.md` now annotates exactly one unmet bar and silently asserts two others, so a reader cannot tell which capabilities actually hold.
  evidence: The Success signal was amended to state its instructing-files half is unmet. Two other bars are also not met and carry no such note. CAP-3's success ("an end-of-life build SDK emits NETSDK1239") is unmeetable by any shipping SDK — the retrospective graded it **half-met** and `spec-1-3` documents the property as inert until .NET 11 GA at the earliest. CAP-1's success depends on "a deliberately broken codec test turns that job red", and that proof is superseded evidence describing a tree with unpinned packages and the old `Directory.Build.props` (retro finding S3, now issue #16). The asymmetry was introduced by this correction: annotating one bar and not the others implies the unannotated ones hold. Either annotate all three consistently or move capability status out of `SPEC.md` entirely into the retrospective's verdict table, which already carries it.

- source_spec: `_bmad-output/implementation-artifacts/spec-epic-1-record-corrections.md`
  summary: CAP-1 was left branch-unqualified while CAP-2 and the Success signal gained branch enumerations, so the per-branch falsifiability `decision-q3` asked for is only two-thirds applied.
  evidence: `decision-q3-name-branches-in-spec` scoped its amendment to CAP-2, and the correction extended that to the Success signal because that is where the ambiguous repo-wide wording actually lived. CAP-1's success clause still says "A CI job runs `dotnet test CairnMultiplayerShared.Tests -c Release`…" with no branch named — the same ambiguity, in the capability the epic exists to deliver. Retro finding S1's lesson was general ("an epic whose success signal says 'the repo' should state which branches it means"), not CAP-2-specific. Extending it to CAP-1 was outside the approved scope of the records correction, which is why it is here rather than done.

- source_spec: `_bmad-output/implementation-artifacts/spec-epic-1-record-corrections.md`
  summary: The branch list `develop`/`production`/`next/feature-framework` is now hardcoded in three places with no rule keeping them in sync.
  evidence: It appears in CAP-2's success clause, in the amended Success signal, and in `.github/workflows/ci.yml:13,15`. Nothing states that the three must move together, so adding a fourth long-lived branch gives it CI coverage or the CAP-2 bar but not necessarily both — and the bar it would escape is precisely the one just made falsifiable per-branch. An alternative considered and not taken: phrase the spec clauses as "every branch listed in `ci.yml` `push.branches`" so the workflow is the single source of truth. That was not adopted because the explicit enumeration was the wording chosen when `decision-q3` was answered, and silently swapping it for an indirection would change the approved contract. Worth deciding at triage.

- source_spec: `_bmad-output/implementation-artifacts/spec-epic-1-record-corrections.md`
  summary: The amended Success signal excludes all of `_bmad-output/`, which is broader than the historical-records exemption it is meant to express.
  evidence: The exclusion exists so the bar is not satisfied by the retrospective, `spec-1-2` and the research report, which quote "You can now commit these files" in order to document it — a bar satisfiable by its own evidence is no bar. But `_bmad-output/` is not only history: it also holds specs and epic-context files that agents read as instructions. A future spec or context file under that tree telling an agent that `game-refs/` is committed would escape the bar entirely. Tightening the exclusion to records for work already closed (stories at `status: done`, dated retrospectives) would keep the intent and close the hole. Left as written because narrowing it is a change to the contract wording rather than to a record.

- source_spec: `_bmad-output/implementation-artifacts/spec-epic-1-record-corrections.md`
  summary: `resolved_by` is a scalar but action item 1 was resolved by two decisions, so the board silently records only one of them.
  evidence: Item 1 carries `resolved_by: "decision-q1-production-in-scope"`. `decision-q4-promotion-carries-everything` also declares `resolves: "epic-1-retro-item-1-carry-epic-1-to-production-or-state-that"` — it settled the sub-question q1 left open, namely whether the promotion should carry the whole `_bmad-output/` tree. Reading the board alone, q4's role in closing item 1 is invisible; it is recoverable only by reading every decision's `resolves` field in reverse. Either make `resolved_by` a list or treat the decisions' own `resolves` fields as authoritative and drop the back-pointer. Schema question for whoever owns the board format, not a defect in any one item.

- source_spec: `_bmad-output/implementation-artifacts/spec-epic-1-record-corrections.md`
  summary: Ledger entries have no ids, dates or stable handles, so cross-referencing between them can only be done in prose — and this correction had to restate two entries rather than link them.
  evidence: The entry above recording the unmet Success signal has to identify its siblings as "the `package-mod.ps1` '(versioned)' entry" and "the `Directory.Build.props` 'versionné' entry", because there is nothing else to point at. The header forbids editing or de-duplicating existing entries, which is right for an append-only log, but it means every refinement restates its subject and the ledger grows by repetition — already visible in the duplicate-run entry and its PR #13 refinement, which the latter says should be "folded into the original at triage" precisely because it cannot link. Adding an `id:` to new entries (and leaving old ones alone) would let future appends reference rather than restate. Format change, so it belongs to triage.

- source_spec: `_bmad-output/implementation-artifacts/spec-epic-1-record-corrections.md`
  summary: This correction inserted lines into `spec-1-1`, so the retrospective's own by-line citations into that file no longer land where they point.
  evidence: `epic-1-retro-2026-08-14.md` cites `spec-1-1:92` for the "push not yet exercised" note and `spec-1-1:46`/`spec-1-1:42` for the two Code Map anchors. The correction added a Spec Change Log section of roughly seven lines above them, so the evidence paragraph now sits near `:99` and the Code Map bullets shifted with it. The retrospective is a dated record and was deliberately not rewritten — the same convention that makes it trustworthy also makes its line citations perishable, which is the identical failure mode it raised about `spec-1-1` in finding S4. The general fix is to cite by heading or quoted text rather than by line number in records meant to outlive the files they cite; the specific one is a note in the retro. Both are triage calls.
