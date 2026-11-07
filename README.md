<div align="center">

# CairnMP

**Host-authoritative multiplayer for the climbing game _Cairn_.**

[![CI](https://github.com/CairnMP/cairnmp-mod/actions/workflows/ci.yml/badge.svg?branch=develop)](https://github.com/CairnMP/cairnmp-mod/actions/workflows/ci.yml)
[![CodeQL](https://github.com/CairnMP/cairnmp-mod/actions/workflows/codeql.yml/badge.svg?branch=develop)](https://github.com/CairnMP/cairnmp-mod/actions/workflows/codeql.yml)
[![License: AGPL-3.0](https://img.shields.io/badge/license-AGPL--3.0-blue.svg)](LICENSE)

[Features](#features) · [Player guide](#player-guide) · [Build](#build-and-test) ·
[Contribute](CONTRIBUTING.md) · [Security](SECURITY.md) · [Changelog](CHANGELOG.md)

</div>

---

CairnMP runs inside Cairn through MelonLoader (IL2CPP) and connects players over
a Steam relay (P2P). The lobby owner is authoritative, so no dedicated server is
required.

Public lobby hosts also publish a small presence record to the CairnMP platform.
The mod refreshes it every 30 seconds and removes it when the host leaves; the
platform expires missing heartbeats after 90 seconds. This powers community
websites without moving matchmaking or gameplay traffic away from Steam.

No shared API key is embedded in the mod. Each lobby receives a short-lived,
random registration token, while community integrations use separate read-only
keys created in the CairnMP developer console. Developers can override the API
base URL for local testing with `CAIRNMP_API_URL`.

> [!IMPORTANT]
> Every player in a lobby must use a compatible CairnMP build. The current wire
> protocol is **27**; lobby admission may also require the exact mod version.

## Features

| Area | Included functionality |
| --- | --- |
| Climbing | A cooperative rope between partners; your own pitons protect your falls |
| Players | Position, animation, cosmetics, lamps, hand and finger poses |
| World | Weather, time of day, pitons, shared ground items, and world pings |
| Communication | In-game chat, completion, host commands, and experimental proximity voice |
| Sessions | Steam relay hosting, late-join state, and host-authoritative validation |
| Modes | Rope team, Belay Team, free solo and race, with synchronized bivouac behavior |
| Destinations | Mount Kami from your own save, or a Summit-mode world from the free *On the Trail: Deep Water* DLC |
| Reliability | Local crash reports, bounded diagnostics, and isolated feature failures |

## Player guide

### Controller

CairnMP uses **View/Share/Create** as a modifier so its commands do not replace
Cairn's normal controller layout. Cairn's native actions are temporarily blocked
while it is held.

Hold `View/Share` to open a radial menu. Aim with the right stick, then release
`View/Share` to choose the highlighted action. Release with the stick centred to
choose nothing.

| Radial menu item | Action |
| --- | --- |
| Multiplayer panel | Open or close the multiplayer panel |
| Attach or detach rope | Attach or detach the cooperative rope |
| Ping | Place a multiplayer ping |
| Show or hide names | Show or hide player names |
| Pick up shared item | Pick up a shared ground item |

Prefer button combinations? In the game's settings, open the **CairnMP** page and set
**Shortcut style** to *Button combos*. Hold `View/Share` and press `Y/Triangle` (rope),
`D-pad ↑` (names), `A/Cross` (pick up), `RB/R1` (ping) or `Menu` (multiplayer panel). The same page lists the shortcuts for the
style you have chosen.

A few actions stay as button combinations in either style because they are held or
repeated, or because they only apply in the backpack.
Hold `View/Share` first, then press the button; the menu steps aside.

| Controller shortcut | Action |
| --- | --- |
| `View/Share` + `X/Square` | Give the selected backpack item |
| `View/Share` + `B/Circle` | Drop the selected backpack item |
| `View/Share` + D-pad `←` / `→` | Select a shared ground item |
| Hold `View/Share` + `LB/L1` | Push to talk |
| `View/Share` + right-stick click | Emergency input release |

The multiplayer panel supports D-pad/stick navigation, confirm, and `B/○` to
close. Text chat and manual lobby-code entry still require text input; public
lobby browsing, hosting, gameplay actions, and voice are controller-accessible.

### Chat

| Key | Action |
| --- | --- |
| `Enter` | Open chat and send a message |
| `↑` / `↓` | Browse previously sent messages |
| `Tab` | Complete a command or player argument |
| `Shift` + `Tab` | Cycle completion backward |
| `Escape` | Close chat |
| `F10` or `View/Share` + right-stick click | Force-close chat if needed |

Your climber remains stationary while you type. The suggestion bar lists matching
commands and players, including nicknames with spaces, and displays command usage.
Run `/help` for the complete command list.

Feature-provided commands participate automatically when their usage string marks
an argument as `<player>`.

### Share items

Select an ordinary consumable in Cairn’s backpack. The native action bar adds:

- **`G` or `View/Share` + `X/Square` — Give nearest:** transfer one unit to the nearest eligible player;
- **`X` or `View/Share` + `B/Circle` — Drop:** place one unit into the shared world.

| Rule | Limit |
| --- | ---: |
| Give distance | 3.5 m |
| Pickup distance | 1.6 m |
| Shared ground items | 32 per session |
| Quantity moved per action | 1 |

The give action is dimmed when nobody is eligible. Both players must be active in
the same area. The prompts are disabled while an item is being dragged or the bag
is busy.

Dropped items are owned by the host, synchronized to every player, and picked up
with **`E`** or the radial menu's *Pick up shared item*. If the recipient’s backpack is full, the item returns to the ground
instead of being destroyed or duplicated.

> [!NOTE]
> Quest items, containers, charms, unique objects, and equipment with individual
> state cannot be shared. Items can only leave the selected backpack stack; there
> is no chat command for giving or dropping.

### Belay Team experiment

Choose **Belay Team** when creating a lobby to enable partner belay. Rope team
keeps its original direct harness tether; Belay Team rejects that tether in input,
host validation, received packets and late-join snapshots. Every peer
must use the same experimental build (protocol **27**, incompatible with the
public 2.4.1/2.4.2/2.4.3 protocol-16 builds and earlier pilots). This branch
includes upstream 2.4.3, including its Story-launch preset correction, main-menu
label and harness-rope fixes. The upstream
physics/render callbacks remain scoped to the dedicated harness rope, while
Belay Team uses its own protection-rope controller. A missing station attachment can
now be requested on the climber's synchronized native copy of that exact piton.
This implementation has automated coverage; its initial attachment still needs
native two-client validation. Belay Team does not create the direct harness tether.

Press **L** to choose a player. With the panel open, **Up/Down** selects a partner,
**I** requests belay, **O** offers it, **Enter** accepts and **Backspace** declines
or cancels a pending invitation. **K** defines/removes an idle station. Shortcuts
act once per press; Backspace does not end an active pair. Closing L does not
accept an invitation. Gameplay action maps stay blocked while the panel is open,
including mouse climbing; Enter belongs to the panel instead of opening chat.
The belayer must remain personally anchored (**E**) at their native piton station.
**K** can mark that anchor as the station before inviting a partner. The initial
climber does not define a station. When their native rope already includes the
belayer's exact healthy, synchronized station piton in the validated route, they
can accept without using **E**. Connecting a station that is not yet on that rope
instead requires the climber to remain personally anchored with **E** at a separate
healthy backup until attachment is confirmed. Both players must be in the same
world. The fixed 6 m invitation limit is replaced by the climber's
native rope capacity and protection-route checks. The climber declares capacity
when requesting or accepting; an offer waits for that declaration. The host uses
a bounded distance check with a 3 m allowance for replicated body/attachment
offsets, while native station-to-backup geometry, ordered route and remaining
rope remain decisive before control. Chat fallbacks remain available:
`/belay request <player>`, `/belay offer <player>`, `/belay accept`, `/belay stop`.
**Space** (or F7) toggles the latched brake, starting with free rope. Only one pair
can use this pilot at a time. L opens this panel only in Belay Team; Rope team
keeps its direct-tether shortcut.

A separate HUD line identifies your confirmed role as **SEGURADOR / BELAYER** or
**ESCALADOR / CLIMBER**, with your partner's name. It shows **CONECTANDO / CONNECTING**
until control is ready, and keeps the previous roles during a proposed exchange
until the host commits it.

With the panel closed, the belayer holds **Up** to feed rope or **Down** to take
in slack at up to 1 m/s. Feeding requires FREE; take-in also works while BRAKED,
but stops at the loaded/geometric rope length rather than hauling the character.
Release the key to stop; holding both directions cancels motion. Opening the
panel, typing or captured input sends zero motion. The panel and HUD show
the climber's reported slack, reserve, motion input and native length readback,
separately from the host-accepted request. Those reports are not independent
proof of physics.

The belayer also receives a bounded, visual-only copy of the climber's native
rope shape from the agreed station, through each protection, to the harness.
Samples refresh at up to 10 Hz, with at most 192 points and interpolation between
updates of the same topology. They do not create another physical rope or alter
damage, brake input or the session lease. Stale drawings disappear after 0.75 s;
ending the pair or resetting the scene clears the drawing. This visual candidate
now selects physical parts by their actual consecutive attachment pins, allowing
repeated Climbot holders outside that path. This correction still needs two-client
validation for visibility in both roles, slack and protection updates.
The drawing clones the belayer's local native rope material and LineRenderer
style, preserving its texture/UV settings instead of applying a player-palette
color. A supported flat fallback can be replaced when native styling becomes
available; material diagnostics identify which source is in use. Only owned
clones are destroyed. The corrected appearance still needs native confirmation.

Held motion is renewed at up to 10 Hz and expires after 0.4 s without a fresh
accepted command on the climber. This stops explicit feed/take-in, while retaining
the latched brake; FREE can still pay out naturally as the climber moves or falls.
The three-second session lease remains separate. The host permits a two-command
delivery burst, bounds sustained input and retries the latest sampled keys after
rate limiting. It never retries a saved old motion. A protection-route transition
cancels held motion until a fresh post-rebind command arrives. The command path
and budget/expiry behavior have simulated participant coverage; native keyboard,
network latency and two-client behavior still need validation.

The existing personal piton anchor can also be explicitly marked with **Define
station** in the L panel, even before a partner joins. **Remove station** clears
an idle station or cancels its pending invitation; it does not detach the personal
anchor or retrieve the piton. Releasing the native anchor, losing/breaking/moving
the piton, leaving Belay Team or changing scenes invalidates the station. Offering
belay or accepting a request still records the current anchor when needed.
Defining a station does not automatically create a pair or exchange roles.
With the L panel open, **K** performs its visible Define/Remove station action
without needing the mouse. One press performs one action; holding K does not
toggle repeatedly. Outside the panel, the shortcut does not consume K. The panel
keeps the cursor visible/unlocked while open and restores its previous state on close.

After acceptance, the belayer's native personal-anchor input is locked and K
cannot remove the active station. Use **L > End belay** before releasing that
anchor manually or returning to climbing. Only the anchored belayer's climb-entry
action is captured; camera, panel and rope controls remain available. The station
is tied to its real piton rather than
a frozen body coordinate, so ordinary anchored body motion stays inside the
native tether area. Piton failure, lost anchoring, departure and disconnect still
end control. This new input guard requires native validation.

The current climber can personally anchor at a separate healthy upper piton,
then choose **Define station / swap roles (K)** in L. The current belayer accepts
with **Enter**, or either participant cancels the proposal with **Backspace**.
The original pair stays active during preparation. Both personal anchors must
remain attached and both endpoints must confirm readiness before the host commits
a new control session with reversed roles and a closed brake. The future climber's
own rope capacity and native station route are checked before transfer. If the
station needs insertion, the existing native preparation/confirmation is reused
while the old belayer remains anchored. Cancellation retains the original roles;
it does not undo a native operation already queued. L stays open during preparation.
After commit, wait for new rope readback before releasing the follower's personal
anchor. This is an experimental anchored exchange, with automated participant
coverage. One matched R23 exchange remained active for about 159 seconds, but
reverse preparations failed. A later matched R35 session committed three
consecutive exchanges in both directions; its final pair remained active for
about 407 seconds until an explicit stop. This is scoped evidence with the same
person hosting, not acceptance of the full exchange/lifecycle matrix. The current
damage, material and station-input corrections still require native retesting.

An unloaded personal-anchor transition may recreate rope segments without adding
a piton. The controller restores only the original ordered holders/protections
and physical pin walk after settling, preserving paid-out rope and the brake.
An unloaded recovery may also remove older pitons before the station only if the
entire station-to-harness holder, segment and physical pin suffix stays unchanged.
Loaded route changes, downstream removals, reorders and incomplete pins are refused.

Invitations carry the station's placer/source id and its lifecycle reference. The
host checks that identity against its live piton registry; the climber resolves
the corresponding native copy, with position used only as a secondary check.
For a missing attachment, the climber must first be personally anchored at a
separate healthy backup reachable within their native rope capacity. Acceptance requests one native
quickdraw insertion into the existing protection route. The panel reports
preparation while the original backup, ordered holders, native piton collection
and loaded pin connections are checked for five observed seconds. Feed/take-in
remain inactive during preparation; only fresh commands after confirmation can
move rope. No extra piton is spawned by this attachment and cancellation does not
rewind an operation already queued in the game. Replacing the station requires
a new invitation. Both invitation directions use this station-bound controller.
Native geometry can insert the station before or after the original protections,
in both initial connections and exchanges. Both orders require the same retained
backup, original holder walk, physical pins and confirmation interval. Protections
before the station remain attached but are outside its controlled suffix; only
the actual station-to-harness path contributes downstream protections and budget.
An already attached station can bind directly after its existing route validates.
The route budget includes the fixed path from that station through downstream
protections; FREE pays out as needed and BRAKED limits falls and outward climbing
using the paid-out length. Remote feed/take-in uses this same bounded budget;
native acceptance of the anchored role exchange remains pending. The R12 H1 session exercised remote
controls, climbing limits and short catches, but also exposed premature session
ends. Its two initial-insertion attempts failed; later sessions used an existing
attachment. These stability changes and initial insertion need native retesting.

Piton synchronization also creates native copies that may be saved by the game.
Existing save duplication is not repaired by this pilot. References inferred only
from position after a reload cannot define/bind a station; use a freshly placed
piton rather than selecting a nearby duplicate. Save/reload and duplication checks
are included in [the cooperative test plan](BELAY-TEAM-TESTS.txt).

Multiple protection pitons are accepted. Adding one piton while unloaded waits
for a complete stable native split before rebinding the final segment without a
new invitation. The same station-route budget rules as the local rehearsal apply;
removal/reordering is not supported. A topology change during suspension ends
ownership instead of switching the loaded constraint to an unverified route. Native
piton failure is observed once and retains its original result. Returning to the
wall rearms the next fall with a new application generation and current intent.

The HUD distinguishes host-accepted intent from the climber's measured suspension
readback. Input permission expires after three seconds without renewal. A valid
owned suspension requests hold at its current geometry before ownership ends;
scene/invalid loaded-topology changes invalidate old native references without restoring an old
radius. Pause, cutscene or focus loss ends the invitation/session; return to
gameplay, re-anchor and send a new invitation. Automatic suspension/resume is a
later milestone, not a function of this pilot. The two-minute diagnostic limit
applies to solo exercises; partner sessions instead require fresh input permission.
Native two-client braking, station
integration, damage/failure coherence and crash stability still require testing.
Remote control does not depend on a diagnostic capture or its recording limits.
The opt-in `Debug.BelaySharedDamageExperiment` adds a cooperative fall-damage
experiment to this build. After a measured catch remains suspended for one second,
the climber reports the vertical drop, station-to-climber rope budget and observed
native fall HP loss. The host validates the current pair and approves at most one
addition per catch. The existing height-attenuated curve is retained: low falls
receive less damage even at a high estimated fall factor. Native fall loss is
credited against that target; exhaustion loss is not credited as fall damage.

After the climber confirms the completed native call and its actual HP change,
the host sends the belayer 30% of the climber's observed native-plus-added fall
loss, normalized by the climber's maximum HP and applied to the belayer's maximum
HP. Both doses use the game's native Fall damage path, preserving difficulty,
protections and death handling. A zero addition consumes the catch without a
damage call. Incomplete calls, expired sessions and changed roles cannot issue
a stale or duplicate dose. Exchanges wait for a pending damage decision to finish
or expire. The HUD and `[BelayDamage]` logs report both local requested and observed
damage. The host validates bounded reports from the authorized climber; it does
not independently simulate that client's physics. This combined build still
requires native two-client validation. A forced belayer ragdoll is not implemented.

`Debug.BelayRouteExperiment` also enables an independent solo exercise: Ctrl+F11
starts/ends control and Space/F7 brakes/releases. It uses the same native adapter
and shows piton count, route generation and applied distance.
The inactive banner shows any rejected start reason for 15 seconds, so a failed
Ctrl+F11 attempt is visible without opening the log. A geometric preview
calculates station -> ordered pitons -> climber capacity, with the first piton
standing in for a simulated station. That preview does not attach or reroute the
physical rope. The earlier solo brake trial and empty-panel trial provide partial
native evidence; populated panels and real partner operation remain unverified.

`Debug.BelayRopeExperiment` enables a separate **local rope-budget exercise**,
off by default. It takes precedence over the previous solo exercise. Start/end
with Ctrl+F11 while stable on the wall; Space/F7 toggles the brake. Hold Up to feed
rope or Down to take in slack at 1 m/s. Pausing or switching windows ends the
exercise; the inactive banner and local log report the stop reason. An open brake
permits passive payout in a fall up to the rope-end cap. Closing the brake preserves the released length,
including existing slack. Take-in is one-way and cannot shorten a loaded segment;
the brake blocks additional manual feed. Releasing either arrow stops that command.
Open payout follows both geometric and native loaded distance. When braking an
already falling character, the next physics step captures at least the loaded
length once, preserving any additional slack; later steps hold that target.
The end-knot capacity remains the cap even during native stretch. This avoids
rewinding a falling body to an older, shorter geometric measurement; actual catch
motion and rebound still require native validation.

For the local ascent/take-in exercise, Ctrl+Down toggles a simulated belayer
holding take-in for at most 20 seconds, leaving the mouse and movement keys free.
The HUD shows the remaining time. Ctrl+Down again or a manual arrow interrupts it;
pause, lost focus/input capture and ending the exercise also cancel it. This
supplies the same bounded take-in command and does not automate the brake or
change character movement. Native route rejections name the failed mapping guard,
and bounded samples include the actual securing-point position for this trial.

The local experiment also restricts the native climbing-permission decision while
braked: existing slack can be used, but movement farther from the last piton stops
at the paid-out limit. Movement toward it remains possible, including upward
climbing below an upper piton. Release the brake and feed more rope to continue.
The filter preserves native denials and neither changes the coil's total capacity
nor sets character position/velocity. This new native integration needs a separate
above-piton movement test for each changed integration; a scoped offline
block/retreat/release-payout-resume trial passed on local `667fb93`. It does not
yet extend the remote partner pilot.

For an opt-in **local station rehearsal**, anchor yourself on a healthy piton
already attached to your native protection rope, then press Ctrl+F10 to record
it. Release your personal anchor, keep that piton on the same rope and use Ctrl+F11
to start the rope-budget exercise. The captured station must occur exactly once
on the ordered holder walk; intermediate protections retain their order and
repeated occurrences. The HUD reports the station-to-last-protection path.
Its remaining capacity is intersected with the native final-segment allowance,
so station accounting cannot grant additional rope. This rehearsal uses the
existing physical protection chain; it does not attach a partner endpoint or
replace the Climbot. The recorded piton stands in for the absent anchored belayer.
Unlike a real player's station, this solo test marker remains after releasing
your personal anchor. End Ctrl+F11 before Ctrl+F10 clears it; scene changes and
native identity loss invalidate it. With a recorded station, adding one new piton
while stable on the wall now pauses manual length writes, waits for the ordered
native route to settle and rebinds its new final segment. Temporary holder walks
are not accepted as completed clips: the existing protection order plus exactly
one new piton, one additional physical part and one additional holder must be
present before the new mapping can settle. This also waits for the held rope to
be released after attaching the piton. Bounded local diagnostics record the
observed protection sequences. The station-to-protection
geometry plus paid-out final length is conserved; FREE can pay out only the extra
length needed by the new geometry. BRAKED cannot silently gain rope. The brake
state is retained and held take-in is cancelled during the transition. Existing
native permission failures are preserved.

The transition has a three-second deadline. A fall, suspension, missing station,
removal/reordering, insufficient paid-out rope or invalid capacity ends the local
controller. It does not roll back pitons or restore an older rope length. The
exercise without a recorded station still uses a fixed route. This new integration
needs native validation independently of the already-passed fixed two-piton
rehearsal on `1234e4e`; no partner endpoint or role transfer is implemented.

`Debug.BelayAppendGuide` optionally replaces the diagnostic overlay with an
observation-only guide for one offline BRAKED protection addition. It checks the
recorded station and initial route, asks for 3.5 m of confirmed slack before
closing the brake, follows the addition and requires five observed seconds of
BRAKED control with two intermediate protections and matching length readback.
The current instruction, controller ACTIVE/STOPPED state and final PASS/RETRY
remain visible; stopping control, changing scenes or returning to the menu does
not erase a result. A new exercise or station action starts a fresh attempt.
The guide never changes rope commands or native physics. It defaults off;
`Debug.BelayAppendGuideLanguage` defaults to `en`, with `pt` available for the
local tester. A guide pass covers this local workflow only, not partner operation.

This experiment controls the final segment of the existing native protection
route. The HUD separates released length, estimated straight-line slack, geometric
reserve, native rest/particle measurements and the suspension readback. Rope and
suspension coordinates are translated at each new suspension; that experimental
mapping needs matched native fall tests. The budget persists when returning to the
wall. The geometric capacity can change while unloaded without resetting paid-out
length or the current command; suspension retains a fixed capacity until unloaded.
An invalid capacity or one below the paid-out length ends control. Without a
recorded station, a route change also ends the fixed-route exercise rather than
silently resetting its budget. With a locked budget, the climbing permission hook
blocks outward wall movement at the released limit and permits retreat; an
unexpected overrun still ends control without pulling the character back. These
budget rules also apply to station-bound partner control. Native `RefreshRopeLength` consumes the owned
length goal in its normal solver phase, even when Climbot stops issuing instant
setters. The HUD reports whether native rest-length readback matches the requested
length; this is not a fall/visual-equilibrium confirmation. Sampling is bounded
to two records per second, including read-only stretch/compression observations.
No remote take-in, shared station attachment or protected role transfer is added
by this local exercise. Native damage/piton coherence is still unverified.

An independent opt-in **station reconnection trial** is enabled by
`Debug.BelayStationConnectionExperiment` together with `Debug.BelayRopeExperiment`.
Enter Story directly without Multiplayer. Place/anchor at A and record it with
Ctrl+F10, then release personal anchoring, place B about 2 m higher and personally
anchor at B. Keep exactly the native A -> B protection route. Ctrl+F12 requests
unclipping only A's quickdraw from that rope; the second press is enabled after
the game confirms A is absent and B is still attached. It requests native
geometric reinsertion of the same existing quickdraw, then checks the original
protection order, loaded rope parts, native pin-collider references for A/B,
usable station route and five observed seconds of stable connection. The panel
gives the current instruction and retains PASS/NOT APPROVED.
An unmet preflight check instead retains NOT STARTED: no unclip or connection
request was issued in that attempt. The panel distinguishes missing A, invalid
personal B, route preparation, unloaded segment physics and mismatched A/B pins,
and displays the specific check. Bounded per-segment pin readbacks stay in the
local log so an unavailable collider is distinguishable from unloaded physics.
Quickdraw pin checks use the native `GetAttachEnd()` collider on both neighboring
rope parts, rather than requiring the separate `attachBegin` field. Both actual
pin occurrences are required for attached A/B; detached A requires zero, with B
still present twice.
The earlier append guide is suppressed while this trial is selected.
Readiness and the preparation panel use Cairn's native lifecycle rather than
the multiplayer player-state cache, which can remain Menu during offline Story.
Ctrl+F11 cannot start the separate rope-budget controller while this trial is
selected; use Ctrl+F12 and follow the panel.

This diagnostic does not spawn/delete pitons, write inventory/HP/positions or
control rope length/brake. It requires the same healthy personal B anchor, native
rope identity and piton collection throughout. Only one native request is issued
per phase. Native topology changes have a three-second deadline and require a
complete stable snapshot; missing frames do not count toward confirmation. Pause,
focus/mode/scene changes, movement or anchor/route loss stop further requests.
Cancellation does not rewind rope state or clear the game's native request queue;
an already queued operation can still finish. Keep B attached. Ctrl+F12 can retry
a detached A only if the original B anchor and exact remaining route are verified
again. This is a no-fall, solo reconnection experiment; no initial partner
attachment, two-client routing or role transfer acceptance is established.

### Proximity voice

Experimental proximity voice is available under **Settings → CairnMP**. It
includes voice activation, push-to-talk, microphone selection, individual player
mutes, volume control, and a local microphone test.

> [!WARNING]
> Voice supports Windows and Proton through WASAPI, plus native Linux and macOS
> through OpenAL. Two-player quality validation is still in progress. See the
> [complete voice guide](docs/proximity-voice.md).

## Documentation

| Guide | Audience | Purpose |
| --- | --- | --- |
| [Contributing](CONTRIBUTING.md) | Contributors | Issues, setup, checks, and pull requests |
| [Architecture](docs/architecture.md) | Developers | Layer boundaries, packet flow, and conventions |
| [Managed extension API](docs/multiplayer-api.md) | Mod authors | Host-authoritative integration API |
| [Proximity voice](docs/proximity-voice.md) | Players and testers | Settings, implementation, and validation |
| [Performance diagnostics](PERFORMANCE-TESTS.txt) | Developers and testers | Local frame captures and repeatable solo/multiplayer comparisons |
| [Security policy](SECURITY.md) | Everyone | Private reporting and security scope |
| [Changelog](CHANGELOG.md) | Everyone | Version history and compatibility notes |

## Repository layout

| Path | Role |
| --- | --- |
| `CairnMultiplayerMod/Features/` | One-file multiplayer feature declarations |
| `CairnMultiplayerMod/Api/` | Public `CairnMultiplayer.Api` contracts |
| `CairnMultiplayerMod/Bootstrap/` | Startup and composition root |
| `CairnMultiplayerMod/Framework/` | Feature lifecycle and typed channels |
| `CairnMultiplayerMod/GameApi/` | Safe façade for game operations |
| `CairnMultiplayerMod/Internal/` | Cairn, Unity, IL2CPP, Steam, Harmony, and UI implementations |
| `CairnMultiplayerMod.Generators/` | Build-time feature discovery generator |
| `CairnMultiplayerShared/` | Shared packets, constants, and validation |
| `CairnMultiplayer.Tests/` | Portable protocol, architecture, generator, and authority tests |
| `CairnMultiplayerMod.Tests/` | Tests that require game references |
| `examples/` | Managed extension source example |
| `scripts/` | Checks, reference generation, version sync, and packaging |
| `game-refs/` | Local proprietary references; ignored by Git |

See the [architecture guide](docs/architecture.md) for dependency rules and the
inbound packet flow.

## Development

### Requirements

- .NET SDK **10.0.400**, pinned by `global.json`;
- .NET 6 runtime for native mod tests;
- JetBrains Rider for the shared run/debug profiles;
- local Cairn, Unity, and MelonLoader references for the complete mod build.

### Open the solution

Open `CairnMultiplayer.sln` from the repository root in **JetBrains Rider**. It
loads the mod, shared protocol, source generator, and both test projects. The main
Markdown guides appear in the `Documentation` solution folder.

If game types are unresolved, generate local IL2CPP references:

```powershell
pwsh scripts/generate-il2cpp-refs.ps1
```

Generated references remain local and must never be committed.

### Run or debug CairnMP

Select **CairnMP** in Rider’s run-configuration menu. The shared profiles build in
Debug, deploy the DLLs and PDBs, then start `Cairn.exe` with
`--melonloader.debug`.

| Profile or action | Result |
| --- | --- |
| **Run** (`Shift+F10`) | Build, deploy, and launch Cairn |
| **Debug** (`Shift+F9`) | Launch with the CoreCLR debugger |
| **CairnMP - Build Debug** | Build and deploy without launching |

Although Cairn uses IL2CPP, MelonLoader hosts the mod in **.NET 6**. Keep Rider’s
runtime set to **.NET / .NET Core** (`RUNTIME_TYPE=coreclr`), not Auto, .NET
Framework, or Unity/Mono. CoreCLR debugging has been verified at
`Mod.OnInitializeMelon`, including local values; it does not restore the original
C# sources of the IL2CPP game.

Close Cairn before rebuilding or switching between Run and Debug, and keep Steam
open for multiplayer. The shared profiles assume the standard install path under
`%LOCALAPPDATA%\CairnMultiplayerData\game` and the SDK at
`C:\Program Files\dotnet\dotnet.exe`. For a custom installation, update both the
game profile and the `CairnDir` MSBuild property so deployment and launch use the
same location.

## Reference assemblies

The build depends on proprietary Cairn, Unity, and MelonLoader assemblies that
cannot be redistributed. `game-refs/` is therefore ignored by Git.

Choose one setup:

1. place the assemblies under `game-refs/Il2CppAssemblies/` and
   `game-refs/MelonLoader/`; or
2. point `MELON_LOADER_NET6_DIR` at the MelonLoader `net6` directory and let
   `Directory.Build.props` read IL2CPP assemblies from the local Cairn install.

The default IL2CPP location is:

```text
%LOCALAPPDATA%\CairnMultiplayerData\game\MelonLoader\Il2CppAssemblies
```

## Build and test

### Portable suite

From a fresh clone without Cairn installed:

```bash
dotnet restore CairnMultiplayer.Tests/CairnMultiplayer.Tests.csproj --locked-mode
dotnet test CairnMultiplayer.Tests/CairnMultiplayer.Tests.csproj -c Release --no-restore
```

### Complete solution

With game references available:

```bash
dotnet restore CairnMultiplayer.sln --locked-mode
dotnet build CairnMultiplayer.sln -c Release --no-restore -p:DeployMod=false
dotnet test CairnMultiplayer.sln -c Release --no-restore -p:DeployMod=false
```

Normal builds never deploy. Pass `-p:DeployMod=true` explicitly only for a local
development installation.

GitHub Actions runs the portable protocol, architecture, generator, authority,
and diagnostic checks with locked dependencies. The full mod suite stays local
because the proprietary reference assemblies cannot be redistributed.

## Add a feature

A multiplayer feature is a single file. It declares its network messages,
per-frame work, and cleanup. A source generator discovers it automatically—there
is no packet-ID registry or central wiring to edit.

<details>
<summary><strong>View a complete <code>WaveFeature</code> example</strong></summary>

Save this as `CairnMultiplayerMod/Features/WaveFeature.cs`. Pressing **H** displays
`<name> waves!` for three seconds on every other player’s screen.

```csharp
using System.IO;
using CairnMultiplayer.Shared;
using CairnMultiplayerMod.Framework;
using CairnMultiplayerMod.GameApi;

namespace CairnMultiplayerMod.Features;

internal sealed class WaveSent : IPacket
{
    public string FromName = "";

    public void Serialize(BinaryWriter writer) =>
        PacketCodec.WriteString(writer, FromName);

    public void Deserialize(BinaryReader reader) =>
        FromName = PacketCodec.ReadString(reader);
}

internal sealed class WaveFeature : MultiplayerFeature
{
    public override string Id => "wave";

    private const float BannerSeconds = 3f;
    private Broadcast<WaveSent> _wave;

    protected internal override void OnRegister(FeatureBuilder feature)
    {
        _wave = feature.Broadcast<WaveSent>("sent", ShowWave);
        feature.EveryFrame(TickInput, FeaturePhase.Always);
        feature.OnSessionEnded(() => Game.Hud.HideMessage("wave"));
    }

    private void TickInput()
    {
        if (KeyboardCaptured || !Game.Input.WasKeyPressed(GameKey.H))
            return;

        var message = new WaveSent { FromName = LocalPlayerName };
        ShowWave(LocalPlayerId, message);
        _wave.Send(message);
    }

    private void ShowWave(int fromPlayerId, WaveSent wave)
    {
        Game.Hud.ShowMessage("wave", $"{wave.FromName} waves!", BannerSeconds);
    }
}
```

</details>

The generated list is available under `obj/generated/` after a build.

### Choose a channel

| Channel | Use it for | Example |
| --- | --- | --- |
| `Broadcast<T>` | A transient message any player may send | Chat or pings |
| `HostState<T>` | Host-owned state replayed to late joiners | Weather or time |
| `HostCommand<T>` | A client request the host validates | Sleep or appearance changes |
| `HostEvent<T>` | A transient result emitted only by the host | Item delivery results |
| `PerPlayerState<T>` | Host-owned state keyed by player | Appearance |
| `Stream<T>` | Rate-limited transient updates | Finger poses |

Use `HostState<T>` whenever a late joiner must learn a value that is still true.
A broadcast disappears once sent.

### Choose a phase

| Phase | Runs | Appropriate work |
| --- | --- | --- |
| `FeaturePhase.Always` | Menus, bivouacs, and gameplay | Input and HUD |
| `FeaturePhase.Gameplay` | Active gameplay synchronization only | World interaction |

Features use `GameApi` for Cairn operations and `FeatureBuilder` for networking.
Unity, IL2CPP, Steamworks, MelonLoader, and Harmony remain under `Internal`.
Diagnostic `CMP003` rejects feature code that crosses this boundary.

Feature exceptions are logged and isolated so one feature cannot stop the update
loop or unrelated features. For a real implementation, read
[`PingFeature.cs`](CairnMultiplayerMod/Features/PingFeature.cs).

## Managed extension API

Other MelonLoader mods can integrate through the host-authoritative
`CairnMultiplayer.Api` surface. It provides compatibility negotiation, typed
commands, transactions, late-join state, and transient events without exposing
Steam or raw packets.

Read the [managed extension API guide](docs/multiplayer-api.md) and the
[complete source example](examples/ManagedExtensionExample/).

## Diagnostics and privacy

CairnMP never uploads diagnostics. Recoverable problems are deduplicated into
local session logs.

If a fatal mod error occurs, CairnMP stops multiplayer and installed patches,
then creates:

```text
UserData/CairnMultiplayer/Crashes/CairnMP-crash-*.zip
```

Cairn displays the path before closing on request or after 30 seconds. Storage is
bounded to 10 session logs (20 MiB) and 5 crash archives (100 MiB). Oversized
source logs are tailed inside the archive.

## Package a release

```powershell
pwsh scripts/package-mod.ps1
```

Output:

```text
../CairnMP-packages/cairnmp-mod-{version}.zip
../CairnMP-packages/cairnmp-mod-{version}-debug.zip
```

Building `CairnMultiplayer.sln` also refreshes this archive automatically. The
package directory is outside the repository, so generated ZIP files never enter
Git and do not require a `.gitignore` rule. Release and Debug builds use separate
filenames. Set `PackageOnSolutionBuild=false` to disable this hook for a particular
build.

## Publish a release

Pushing a `v*` tag runs `.github/workflows/release.yml`. It builds, tests and
packages the mod, then publishes the GitHub release the launcher installs from:
players are offered it as soon as it appears, with no other step.

1. Bump `mod` in `versions.json` and run `node scripts/sync-versions.js`.
2. In `CHANGELOG.md`, rename `## [Unreleased]` to
   `## [x.y.z] — YYYY-MM-DD (beta)` or `(stable)`.
3. Commit, then `git tag vx.y.z` and `git push origin vx.y.z`.

A `(beta)` heading publishes a pre-release, which only the launcher's beta channel
installs; untick **pre-release** on GitHub to promote it to stable. The release
notes are that changelog section. A tag that disagrees with `versions.json`, or a
version without a changelog section, fails before anything is built. A suffixed
tag (`vx.y.z-r2`) republishes the same version.

Credit first-time contributors under `### New contributors` in that section. Once
published, the notes are posted to Discord if the webhook secret below is set; if
that post fails, **Re-run failed jobs** sends it again without touching the release.

### One-time setup

The build needs the game's generated assemblies, which must never be published:

1. Create the **private** repository `CairnMP/cairnmp-game-refs`.
2. Fill it from a Cairn install where MelonLoader has run once, then commit and
   push. Repeat after a game update.

   ```powershell
   ./scripts/export-game-refs.ps1 -GameDirectory "<Cairn folder>" -Destination ..\cairnmp-game-refs
   ```

3. Run `ssh-keygen -t ed25519 -N "" -f game-refs-key`. Add `game-refs-key.pub` as
   a read-only deploy key of `cairnmp-game-refs`, and the content of `game-refs-key`
   as the `GAME_REFS_DEPLOY_KEY` Actions secret of this repository.

The loader references come from the MelonLoader archive the launcher installs.
Until LavaGang releases the fix for non-ASCII game paths
([LavaGang/MelonLoader#1226](https://github.com/LavaGang/MelonLoader/pull/1226)), that is the
[patched fork](https://github.com/yutho-o/MelonLoader/tree/cairnmp)'s
`v0.7.2-cairnmp.N` build; afterwards, the official `MelonLoader.x64.zip`. Its URL
and SHA-256 are pinned in the workflow and in the launcher: change both together.

To announce releases on Discord, create a webhook in the channel's settings and add
its full URL as the `DISCORD_WEBHOOK_URL` Actions secret of this repository. Without
the secret, releases publish as usual and nothing is posted.

## Versioning

`versions.json` is the source of truth for the mod and protocol versions. After
editing it, synchronize generated declarations:

```bash
node scripts/sync-versions.js
```

Bump `protocol` whenever packet layouts, feature contract IDs, or their meanings
change incompatibly. The handshake rejects mismatched protocol versions. A change
of lobby owner ends the session; reconnect through a new lobby.

## Contributing

Start with [CONTRIBUTING.md](CONTRIBUTING.md) for issue quality, environment setup,
verification, and pull-request conventions. Participation is governed by the
[Code of Conduct](CODE_OF_CONDUCT.md).

## License

Copyright © 2026 Yutho

CairnMP is free software licensed under the
[GNU Affero General Public License v3.0](LICENSE). Derivative works that reuse
this code must follow the AGPL-3.0 terms, including the network-use source offer.

Proprietary Cairn, Unity, and MelonLoader reference assemblies are not part of
this project and are not covered by its license.
