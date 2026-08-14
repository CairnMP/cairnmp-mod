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
