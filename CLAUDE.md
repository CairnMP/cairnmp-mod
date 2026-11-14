# CLAUDE.md

Claude Code reads this file at the start of every session. The project rules
live in [AGENTS.md](AGENTS.md), shared with every other AI tool, and are
imported here:

@AGENTS.md

---

## Claude Code specifics

The notes below only apply to Claude Code. They add to AGENTS.md and never
relax it.

### Working style

- **Plan before you edit** when a change touches networking, host authority,
  rope, pitons, belay, bivouacs, saves, Harmony patches, or a compatibility
  contract. Use plan mode, show the plan, and wait for approval.
- For a small, well-understood fix, a short summary of the intended change
  before editing is enough.
- Use the Explore subagent for wide searches across the codebase. Read the
  files you will change yourself. Don't fan out several agents for a task one
  session can handle.
- Answer in the language the human writes in (English or French). Code,
  comments, commit messages, the changelog and repository documentation stay
  in English.

### Verification

- Run the portable checks from AGENTS.md section 11 after every code change,
  and quote the real result: the test count, or the failing test and its
  message.
- Before checking whether the complete solution can build, look for
  `game-refs/` or `MELON_LOADER_NET6_DIR`. If neither is there, say the mod
  build was not run. Don't try to work around missing proprietary references.
- Pass `-p:DeployMod=false` to every build or test command. Never pass
  `-p:DeployMod=true` unless the human asks for a deploy.
- Once a change works, reread the diff and simplify it before reporting
  (AGENTS.md section 12). For a larger diff, run `/simplify` on it.
- For non-trivial diffs, offer `/code-review` before the human opens a pull
  request. When packet handling, authority, rate limits or file I/O changed,
  offer `/security-review` as well.
- Claude Code cannot run Cairn. End gameplay changes with the in-game test plan
  from AGENTS.md section 19.

### Git

- Commit only when asked, staging explicit paths with `git add -- <paths>`.
- Never push, open a pull request, comment on GitHub, tag or release on your
  own. When the human asks for a pull request, fill in every section of
  `.github/PULL_REQUEST_TEMPLATE.md` and leave unrun checks as **Not run**.
- The working tree often holds the maintainer's uncommitted work. Leave files
  you were not asked to change alone, even if they look unfinished.

### Local configuration

- `.claude/` is ignored by Git, so Claude Code settings, commands and agents in
  it stay on your machine. Put personal permissions in
  `.claude/settings.local.json`.
- Put personal notes for Claude in `CLAUDE.local.md` at the repository root.
  Git ignores it: never commit it or force-add it.
- Never store credentials, Steam IDs, tokens or private logs in memory files,
  notes or settings.

### Environment notes

- Maintainers work on Windows (Rider, PowerShell) and Linux (Proton, the
  Goldberg emulator). PowerShell scripts need `pwsh`, and `scripts/check.sh`
  needs `bash`.
- `*.ps1` files use CRLF line endings. Keep them when you edit a script.
- `Directory.Build.props` defaults to Windows paths under `%LOCALAPPDATA%`. On
  Linux, the game references come from `game-refs/` or environment variables.
