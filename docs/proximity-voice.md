# Proximity voice (experimental)

Open **Settings → CairnMP**, from the main menu or the pause menu. The page uses
Cairn's native settings fields, scrolling, navigation and reset controls.

- **Voice mode:** open microphone with level-based voice detection by default;
  push-to-talk and microphone mute are available. Muting your microphone does not
  mute incoming voices.
- **Microphone:** Windows communications default or a named input device. Devices are refreshed
  every five seconds on a worker thread. An unplugged selected device stays selected and stops capture;
  it does not silently switch to a different microphone.
- **Detection threshold:** -40 dB by default, adjustable from -60 to -10 dB.
  A lower value picks up quieter speech, but also more background noise. This first
  version uses an RMS gate, not speech classification, echo cancellation or noise suppression.
- **Push-to-talk key:** V by default, configurable in the dropdown.
- **Voice volume:** 0–200%, default 100%.
- **Test microphone:** plays the selected microphone locally. No test audio is sent
  over the network. Closing the page stops the test. Headphones avoid feedback.
- **Mute a player:** individual mute toggles appear for players present when the
  page opens. These are temporary and clear on scene/session reset or player leave.

Preferences persist in the loader preferences under `CairnMP.Voice`. Capture is
stopped outside gameplay, when muted, on loss of application focus, on scene reset
and on disconnect. The explicit local test can capture while the settings page is
open. Transmission is suspended while the CairnMP settings page is open.

Voices attenuate with distance from the local climber, becoming inaudible at 30 m.
Left/right direction follows the camera. Only remote climbers with an available
in-game avatar are played. No wall occlusion or elevation/HRTF filtering is applied.

## Implementation

`VoiceFeature` declares an unreliable `voice.opus-v1` feature stream and delegates
capture/playback to `Game.Voice`. Cairn disables Unity Audio for Wwise, so the mod uses
Windows shared-mode WASAPI through NAudio.Wasapi 2.2.1. Capture is converted by Windows to
24 kHz mono PCM, encoded into 20 ms Opus frames at 24 kbit/s using Concentus 2.2.2, and
relayed through the existing Steam host. Each packet carries burst and sequence
identifiers. Payloads are bounded to 400 bytes, below the gameplay transport's
reliable fallback threshold. Receivers gate playback by proximity; the host currently
relays to all admitted peers. Proximity is an audio effect, not a confidentiality boundary.

The gate retains 60 ms of audio before activation and 250 ms after the signal falls
below the threshold. A 60 ms packet reorder buffer and bounded PCM queue absorb
jitter without accumulating stale conversation after a stall. Loss uses Opus packet
loss concealment. Audio callbacks only read the synchronized PCM buffer; scene
access and codec operations stay on the Unity main thread. WASAPI input and output
callbacks only touch managed, synchronized sample buffers. Device selection stores
the stable endpoint ID; friendly names are only labels. Output uses Windows' default
render device. Native Linux/macOS audio backends are not implemented; Proton is unverified.

Deploy/package `Concentus.dll`, `NAudio.Core.dll`, `NAudio.Wasapi.dll` and
`THIRD-PARTY-NOTICES.txt` with the two mod DLLs.
The build and packaging scripts include these automatically. Both players should
use the same build to test voice; older builds have no handler for this stream.

## Verification

Automated tests cover frame validation, an audible Opus round trip, packet loss,
reordering, sequence wrap, bounded backlog and voice gate release. These tests do
not prove microphone/audio device interoperability or Steam latency.

An in-game main-menu probe verified the native tab, field binding, mode persistence
after reopening, microphone dropdown labels, stable device selection, Windows PCM
capture, output consumption, local-test isolation and capture stopping on close.
This does not establish audible quality or a two-player end-to-end connection.

Before release, test with two Steam accounts: host→guest and guest→host, simultaneous
speech, walking beyond 30 m and back, changing microphone during play, unplug/replug,
voice activation in a noisy room, push-to-talk while typing, focus loss, pause menu,
scene changes, leaving/rejoining and a third late joiner. Listen for missing word
beginnings, clicks and accumulated delay. Validate the pause-menu page separately
from the main-menu page.
