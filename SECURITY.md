# Security policy

## Reporting a vulnerability

Do not disclose vulnerabilities, exploit code, credentials, or unredacted crash
logs in public issues or pull requests.

Use [GitHub private vulnerability reporting](https://github.com/CairnMP/cairnmp-mod/security/advisories/new)
when the repository offers **Report a vulnerability**. If that option is not
available, contact the maintainer privately at **contact@yutho.fr** with the
subject `CairnMP security report`. Never send passwords or live tokens.

Include the affected version/commit, Cairn and MelonLoader versions, impact,
reproduction steps in a session you control, and a minimal proof of concept.
Redact player identifiers, IP addresses, local usernames, and session details.
Coordinate public disclosure with the maintainer after a fix is available.
This is a volunteer project; no response deadline or bug bounty is promised.

## Supported versions

Security fixes target the latest published mod release and the current default
branch, `develop`. Older releases and experimental branches do not receive
separate backports. Development builds are not a stability guarantee.

## Scope and trust boundaries

Relevant issues include unauthorized host actions, malicious packet handling,
resource exhaustion, unsafe file access, arbitrary code execution, and exposure
of player data or credentials. Only test installations and sessions you own or
have explicit permission to test; do not disrupt public lobbies.

Treat every peer and every network payload as untrusted. Validate sender identity,
host authority, lengths, counts, numeric ranges, and session state before applying
an effect. Bound memory allocation, message rates, queues, and parsing work.
Never deserialize network data into arbitrary executable types.

Managed extensions and mods execute locally with the game's permissions; they
are **not sandboxed**. Install only code you trust. Steam relay transport does not
make a peer's messages trustworthy. Back up saves before testing development builds.

## Dependencies and releases

The mod currently targets .NET 6 for loader compatibility. This is a legacy
runtime constraint, not a claim of current runtime security support. Review the
loader/runtime upgrade path before changing the target framework.

Review dependency and GitHub Action updates before merging. Never publish game
reference assemblies, credentials, private logs, or builds from unreviewed pull
requests. See [maintainer setup](docs/maintainer-security.md) for GitHub controls
that must be enabled separately from this file.
