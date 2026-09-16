# Performance diagnostics

This first implementation measures low frame rates and intermittent stalls. It
does not change graphics, physics, gameplay, network rates or native game code.
No performance improvement or game/mod bottleneck has been established yet.

## Internal recorder

After starting the updated mod once, close the game and edit the existing
MelonLoader preferences file in that installation's `UserData` directory:

```toml
[Debug]
PerformanceDiagnostics = true
PerformanceScenario = "route-A / save-A / run-1 / voice-off / rope-off"
```

Preserve any other entries in `[Debug]`; do not create a duplicate section.
Restart Cairn, load the reference save, focus the game and press **Ctrl+F8**.
The recorder waits 30 seconds, measures for 180 seconds and stops automatically.
Ctrl+F8 again ends a capture early. Solo works without joining a lobby.
Set `PerformanceDiagnostics = false` to disable the diagnostic (the default).
There is no player-facing performance option in this diagnostic-only release.

Reports are written locally to:

```text
<game>/UserData/CairnMultiplayer/Performance/CairnMP-performance-*.json
<game>/UserData/CairnMultiplayer/Performance/CairnMP-performance-*.md
```

The console prints the report location once export finishes. Sampling uses
preallocated arrays; sorting, JSON serialization and file writes run after
capture on a worker. A new capture waits for the previous export to finish.
Normal unload waits for a pending export; a forcibly terminated process may
lose it. An export failure is logged and disables diagnostics for that session.

The frame buffer holds 131,072 intervals (8 MiB of numeric arrays). If it
fills before three minutes, the run ends with `buffer-capacity`; it is partial,
not a full run. Up to 256 scene/session events are retained; overflow is counted.
Scene transitions never reset or erase the capture. The last complete interval
is retained even if a stall extends beyond the requested duration. An early
stop/unload does not invent a final partial frame.

### What the report means

- **Update interval:** elapsed time between successive CairnMP `OnUpdate`
  callbacks. It includes intervening game work, waits and rendering cadence.
  It is not a displayed-image timestamp or a CPU/GPU execution measurement.
- **ModUpdate:** elapsed time in the mod's update callback after recorder upkeep.
  Network, Players, Features, Ropes and some Ui scopes are nested within it.
- **Ui:** selected menu/HUD/panel ticks plus `OnGUI` calls between updates. It
  includes time outside ModUpdate; do not sum all columns as a total.
- **Scopes:** synchronous elapsed wall time on the main thread, including native
  calls and preemption. They exclude asynchronous audio/network worker costs,
  uninstrumented patches and native work outside those calls. Zero means no
  measured scope time, not proof that a subsystem has no cost.
- **Statistics:** mean FPS = interval count / total interval seconds; median,
  p95 and p99 use nearest rank. Slow intervals are counted strictly above 33.3,
  50 and 100 ms. Empty data is `null`/`unavailable`, never a fake zero FPS.

The JSON retains aligned per-interval scope arrays, seconds since trigger,
the monotonic start timestamp/frequency, scene/session events and metadata.
Settings queried from Unity are only a subset: record the full game settings,
upscaler and driver overrides separately. GPU/native timings remain unavailable
until an external tool supplies them. This recorder does not collect allocation
or garbage-collection evidence; do not infer GC as the cause of a spike.

## External baseline capture

Use the **same PresentMon console executable** for every condition. Its v1 CSV
export provides present/display intervals; the wrapper selects that format
explicitly and records the executable hash. See the official
[console documentation](https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md)
and [v1 metric definitions](https://github.com/GameTechDev/PresentMon/blob/v1.9.2/README.md#csv-columns).
PresentMon is an external prerequisite, not installed or bundled by CairnMP.

Start the game in the selected condition and find the actual `Cairn.exe` PID
(not the CairnMP launcher). From PowerShell, for example:

```powershell
Get-Process Cairn | Select-Object Id, Path
./scripts/capture-performance.ps1 `
  -PresentMonPath 'C:/Tools/PresentMon.exe' `
  -GameProcessId 1234 `
  -Configuration solo `
  -Instrumentation recording `
  -MachineLabel 'machine-A' `
  -GameBuild 'record the installed build' `
  -Scenario 'route-A / save-A / voice-off / rope-off' `
  -SettingsNote 'attach settings screenshots; record resolution, upscaler, cap and VSync' `
  -Run 1
```

Wait for PresentMon to be ready, focus Cairn, then press **Ctrl+F8 once**. This
triggers both recorders when internal diagnostics are enabled. Do not press it
again during a full run. Both use a 30-second delay and 180-second window, but
their exact boundaries can differ by a frame. Keep raw timestamps for detailed
alignment; the comparison tool does not automatically join internal/external
frames. If PresentMon reports missing ETW permissions, resolve the reported
permission issue before repeating; the wrapper does not elevate itself.

Output defaults to `%LOCALAPPDATA%/CairnMP-Performance/<unique-capture>/`, outside
the repository. It includes raw CSV and a manifest containing hardware, driver,
executable/tool hashes, declared scenario and instrumentation mode. A successful
tool exit is labelled `captured-unverified`: it is not proof of a complete run.
The wrapper changes no installations. Configuration labels are declarations by
the tester and must match the actual loader/mod contents.

## Repeatable benchmark protocol

1. Choose a fixed save and a safe route with three 60-second segments: fixed
   panoramic camera, movement through the environment, then climbing. Record
   the starting camera and segment endpoints in screenshots/video before the
   first run. Reuse the same save, weather/time and route for all comparisons.
2. Keep game build, settings, resolution, power mode, driver, overlays and
   background applications constant. Record frame cap/VSync; use the same
   uncapped setting for throughput comparisons. Keep the game focused. Warm
   caches consistently; measure first-load/cold-cache behaviour separately.
3. Run each condition three times: `vanilla` (no loader), `loader` (no mods),
   `solo` (CairnMP), and a two-machine multiplayer session (`host` and `client`).
   Verify each install before running; do not treat a disconnected lobby as
   vanilla. Use each physical machine's own solo baseline for its multi result.
4. Record voice-off/rope-off first. Repeat separate voice and rope scenarios
   using the same participants/actions. Two instances on one PC are suitable
   for functional checks only, not this benchmark comparison.
5. Measure loading and transitions in separately named captures. Retain their
   events/spikes. Do not silently remove a stall from a steady-state capture;
   flag an unexpected transition and repeat the entire run instead.
6. Quantify diagnostic overhead with separate matched runs using internal
   instrumentation `disabled`, `enabled-idle` and `recording`, always with the
   same external recorder. For enabled-idle, enable diagnostics but **do not**
   trigger it: the wrapper selects a different external hotkey as described below.
7. Compare repeated-run spread before claiming a difference. A useful effect
   must reproduce beyond that spread. Inspect relevant native/managed/GPU
   profiles before attributing a cause or choosing a patch.

For `enabled-idle`, the wrapper uses Ctrl+F7 so it does not start internal
sampling. Use Ctrl+F7 to trigger that external run. All other conditions use
Ctrl+F8. No automatic uploads or hardware identifiers are collected.

## Generate a comparison

Node.js (already used by the repository scripts) is required. Pass one manifest
per recording; include all three repetitions for every compared condition:

```powershell
node scripts/compare-performance.js C:/Temp/CairnMP-comparison `
  C:/Captures/vanilla-1/manifest.json `
  C:/Captures/solo-1/manifest.json
```

The JSON/Markdown report lists each swap chain separately, so menus or auxiliary
windows are not silently merged with the main gameplay stream. Present and
display timings remain distinct. Missing, invalid and zero intervals are
excluded and counted; absent display telemetry stays unavailable. Failed
captures, missing expected columns and multiple recordings in one manifest are
rejected. Inspect partial-duration and metadata warnings before comparing.

The report includes a bottleneck worksheet: reproduction, evidence, confidence,
and next investigation/correction. It does not rank unverified causes or invent
before/after gains. Complete it only after actual captures and profiling.

## Validation status

Automated tests cover statistics, warmup, interval/scope alignment, terminal
stalls, saturation, early stops, scene events, report export, zero managed
allocations in the core sampling loop, and external CSV parsing/reporting.
These tests do not establish live capture overhead or in-game correctness.

Live checks still required: Ctrl+F8 in native solo and multi; complete three-minute
captures; scene change and normal unload while recording; external/internal
trigger alignment; instrumentation overhead; the four-condition comparison on
two machines. No runtime captures or causal performance findings are included
in this implementation.
