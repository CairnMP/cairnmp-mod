# Digest — D2 .NET 6 EOL relevance — round 1

Question owned: does .NET 6 being out of support create real risk for a mod that does not ship the runtime?
Accessed: 2026-08-12

## Claims

| # | Claim | Source | Publisher | Pub date | Confidence | Class |
|---|---|---|---|---|---|---|
| 12 | .NET 6 reached end of support on 2024-11-12 (released 2021-11-08, 3-year LTS window) | https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core | Microsoft | accessed 2026-08-12 | high | version |
| 13 | .NET 6 EOL confirmed independently: "Microsoft no longer provides updates for .NET 6, and security fixes and technical support are no longer available" | https://devblogs.microsoft.com/dotnet/dotnet-6-end-of-support/ | Microsoft .NET Blog | 2024 | high | version |
| 14 | Currently supported: .NET 10 (LTS, until Nov 2028), .NET 9 (STS, until Nov 2026), .NET 8 (LTS, until Nov 2026) | https://learn.microsoft.com/en-us/dotnet/core/releases-and-support | Microsoft Learn | doc updated 2026-06-02 | high | version |
| 15 | "Versions that are out of support no longer receive security updates that protect your applications and data" | https://learn.microsoft.com/en-us/dotnet/core/releases-and-support | Microsoft Learn | doc updated 2026-06-02 | high | policy |
| 16 | MSBuild property `CheckSdkVulnerabilities=true` emits warning NETSDK1239 when the resolved .NET SDK is end of life | https://learn.microsoft.com/en-us/dotnet/core/releases-and-support | Microsoft Learn | doc updated 2026-06-02 | high | tooling |
| 17 | MelonLoader's portable-runtime feature searches the game root for a folder whose name contains "dotnet" and verifies `hostfxr.dll`; documented fallback is "download and execute the **.NET 6** runtime installer as before" | https://github.com/LavaGang/MelonLoader/pull/1062 | LavaGang / JoShMiQueL | accessed 2026-08-12 | high | compatibility |
| 18 | Portable-runtime use case is machines without admin rights (locked-down machines, cybercafés, company PCs) — not runtime modernisation | https://github.com/LavaGang/MelonLoader/pull/1062 | LavaGang / JoShMiQueL | accessed 2026-08-12 | high | compatibility |

## Resolution of the question carried from D1

The "Portable .NET Runtime Fallback" and `<GAME>/dotnet` directories do **not** open a path to a newer runtime. The feature is a deployment convenience for restricted machines, and the documented fallback is explicitly the .NET 6 installer [17][18]. The PR neither restricts nor endorses a newer `hostfxr.dll`, so newer-runtime behaviour is undefined rather than supported — reported as unverified, not as a possibility.

## Notable secondary finding

.NET 8 (LTS) support ends **November 2026** — roughly three months after this research [14]. Any hypothetical migration target of net8 would be near-EOL on arrival; .NET 10 is the current LTS with runway to 2028.

## Leads worth chasing

- `CheckSdkVulnerabilities` [16] is directly actionable for CairnMP's build and belongs in the recommendations, though it flags the **SDK**, not the target framework.
- Whether any CVEs against .NET 6 have been published since EOL that would affect a client-side game process was not investigated this round — candidate for a Deepen.

## Looked for and could not find

- Any MelonLoader statement acknowledging or mitigating the EOL-runtime security position.
- Evidence either way on whether a net8 `hostfxr.dll` placed in a portable folder actually works.
