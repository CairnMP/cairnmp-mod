# Digest — D1 MelonLoader runtime pin — round 1

Question owned: what .NET runtime do current MelonLoader releases load IL2CPP mods into, and is `net6.0` a pin or a choice?
Accessed: 2026-08-12

## Claims

| # | Claim | Source | Publisher | Pub date | Confidence | Class |
|---|---|---|---|---|---|---|
| 1 | Latest MelonLoader release is v0.7.3, published 2026-05-14T20:20:01Z | https://github.com/LavaGang/MelonLoader/releases.atom | LavaGang (GitHub) | 2026-05-14 | high | version |
| 2 | NuGet `LavaGang.MelonLoader` latest is 0.7.3, published 5/14/2026 — corroborates #1 | https://www.nuget.org/packages/LavaGang.MelonLoader/ | NuGet / LavaGang | 2026-05-14 | high | version |
| 3 | IL2CPP games require the ".NET 6.0 Desktop Runtime"; on Windows it is installed automatically | https://github.com/LavaGang/MelonLoader (README Requirements) | LavaGang | accessed 2026-08-12 | high | compatibility |
| 4 | The NuGet package's primary target frameworks are ".NET 6.0" and ".NET Framework 3.5" | https://www.nuget.org/packages/LavaGang.MelonLoader/ | NuGet / LavaGang | 2026-05-14 | high | compatibility |
| 5 | The `net6.0` dependency group is the one carrying Il2CppInterop (Common, Generator, HarmonySupport, Runtime) — IL2CPP modding is the net6.0 surface; the netfx3.5/4.7.2 groups are the Mono surface | https://www.nuget.org/packages/LavaGang.MelonLoader/ | NuGet / LavaGang | 2026-05-14 | high | compatibility |
| 6 | v0.7.3 updated Il2CppInterop to `1.5.1-ci.845` | https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3 | LavaGang | 2026-05-14 | high | version |
| 7 | v0.7.3 changelog: "Implemented support for .NET Portable directories under `<GAME>/dotnet`", "Rewrote .NET Handling to better abide by overrides", "Implemented Portable .NET Runtime Fallback to help minimize HostFxr load failures" | https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3 | LavaGang | 2026-05-14 | high | version |
| 8 | Release cadence: 0.6.6 2024-11, 0.7.0 2025-02, 0.7.1 2025-06, 0.7.2 2026-03, 0.7.3 2026-05 | https://github.com/LavaGang/MelonLoader/releases.atom | LavaGang | 2026-05-14 | high | ecosystem |

## Contradictions encountered and how resolved

- Harness web search and the HTML release pages disagreed on release years by a consistent **two-year offset** (HTML fetch read v0.7.3 as 2024-05-14; NuGet and search read 2026-05-14). Resolved against `releases.atom`, which carries machine-readable ISO timestamps: **2026-05-14 is correct**. The HTML-page readings were a parsing artifact of the fetch summariser, not a real source disagreement. Treat GitHub HTML release-page dates as unreliable in this run; prefer the atom feed.
- Corollary: the two GitHub HTML fetches were not independent corroboration — same publisher, same upstream page.

## Leads worth chasing

- Il2CppInterop `1.5.1-ci.845` is a **CI build**, not a tagged release — suggests MelonLoader consumes Il2CppInterop from CI. Relevant to D3.
- ".NET Portable directories under `<GAME>/dotnet`" and "Portable .NET Runtime Fallback" — does this let a game ship its own runtime, and could that ever be a newer one? Worth one query in D2.
- `melonloader.co` / `www.melonloader.co` failed DNS resolution (ENOTFOUND). Several search hits point there; the live docs appear to be `melonwiki.xyz`, which returned only a title with no body content. Community docs are thin/unreachable — note for D5.

## Looked for and could not find

- Any official statement of a roadmap to .NET 8+ for the IL2CPP host runtime. No roadmap document surfaced; absence is reported as absence, not as evidence there is no plan.
- The MelonLoader wiki's mod-project setup page (target framework guidance) — site returned no body content to the fetcher.
