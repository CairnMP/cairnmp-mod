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
