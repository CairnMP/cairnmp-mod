# Security Policy

> [!IMPORTANT]
> Do **not** disclose vulnerabilities, exploit code, credentials, or unredacted
> crash logs in public issues or pull requests.

## Quick links

- [Report a vulnerability privately](https://github.com/CairnMP/cairnmp-mod/security/advisories/new)
- [Supported versions](#supported-versions)
- [Security scope](#scope-and-trust-boundaries)

## Reporting a vulnerability

Use GitHub’s private vulnerability reporting when the repository displays
**Report a vulnerability**. If that option is unavailable, email
**[contact@yutho.fr](mailto:contact@yutho.fr)** with the subject
`CairnMP security report`.

Include the following information when possible:

- affected CairnMP version or commit;
- Cairn and MelonLoader versions;
- expected security impact;
- reproduction steps from a session you own or are authorized to test;
- a minimal proof of concept;
- any suggested mitigation.

Before sending a report, redact player identifiers, IP addresses, local
usernames, private chat, and session details. Never send passwords or live
tokens.

> [!NOTE]
> CairnMP is a volunteer project. No response deadline or bug bounty is
> promised. Please coordinate public disclosure with the maintainer after a fix
> is available.

## Supported versions

| Version | Security support |
| --- | --- |
| Latest published release | ✅ Supported |
| Current `develop` branch | ✅ Supported |
| Older releases | ❌ No separate backports |
| Experimental branches | ❌ No stability or support guarantee |

## Scope and trust boundaries

Security-relevant reports include:

- unauthorized host actions;
- malicious or malformed packet handling;
- unbounded memory, CPU, queue, or parsing work;
- unsafe file access or arbitrary code execution;
- exposure of player data, private logs, or credentials.

Only test installations and sessions that you own or have explicit permission
to test. Do not disrupt public lobbies.

### Required network assumptions

Treat every peer and every network payload as untrusted:

- validate sender identity and host authority;
- validate lengths, counts, numeric ranges, and session state;
- bound allocations, message rates, queues, and parsing work;
- never deserialize network data into arbitrary executable types.

Steam relay transport does **not** make peer messages trustworthy.

### Local mods and extensions

Managed extensions and mods run locally with the game’s permissions; they are
**not sandboxed**. Install only code you trust, and back up saves before testing
development builds.

## Dependencies and releases

CairnMP currently targets .NET 6 for loader compatibility. This is a legacy
runtime constraint, not a claim of current runtime security support. Review the
loader and runtime upgrade path before changing the target framework.

Before merging or publishing a release:

- review dependency and GitHub Actions updates;
- build only from reviewed commits on protected branches;
- exclude proprietary game references, credentials, and private logs;
- never reuse untrusted pull-request artifacts in a privileged release job.

Branch protection, required checks, review requirements, security features, and
release permissions are administered directly through GitHub. Their live GitHub
configuration is authoritative and should be reviewed periodically by repository
administrators.
