# Proximity Voice

> [!WARNING]
> Proximity voice is experimental. It currently supports Windows through WASAPI;
> Linux, Proton, and macOS are not supported.

Open **Settings → CairnMP** from the main menu or pause menu. The page uses
Cairn’s native settings fields, scrolling, navigation, and reset controls.

## At a glance

| Setting | Default | Details |
| --- | ---: | --- |
| Voice mode | Voice activation | Push-to-talk and microphone mute are also available. |
| Microphone | Windows communications default | A named input device can be selected. |
| Detection threshold | −40 dB | Adjustable from −60 to −10 dB. |
| Microphone enhancement | On | High-pass filtering, automatic gain, compression, and limiting. |
| Push-to-talk key | `V` | Configurable from the settings dropdown. |
| Voice volume | 100% | Adjustable from 0% to 300%. |
| Maximum audible distance | 30 m | Volume decreases with distance. |

## Using voice chat

### Voice modes

- **Voice activation** opens the microphone when the input level crosses the
  configured threshold.
- **Push-to-talk** transmits only while the selected key is held.
- **Microphone mute** stops outgoing voice without muting other players.

Voice activation uses the level measured before automatic gain, so quiet
background noise is not amplified into holding the gate open. Microphone
enhancement can be disabled to send and monitor the finite raw samples. CairnMP
does not provide speech classification, echo cancellation, or noise suppression.

### Choosing a microphone

Select the Windows communications default or a named input device. Devices are
refreshed every five seconds on a worker thread.

> [!NOTE]
> If a selected device is unplugged, CairnMP preserves the selection and stops
> capture. It never switches silently to another microphone.

### Testing and muting

- **Test microphone** plays the processed input locally and displays raw level,
  processed level, and automatic gain. Nothing is transmitted. Closing the page
  stops the test; headphones are recommended to avoid feedback.
- **Mute a player** temporarily silences an individual player listed when the
  page opens. These mutes clear after a scene or session reset, or when the player
  leaves.

Preferences are stored under `CairnMP.Voice`. Normal capture stops outside
gameplay, when muted, after focus loss, on scene reset, and on disconnect. The
explicit local test may capture while the settings page is open, but network
transmission remains suspended there.

## Spatial audio behavior

Remote voices become inaudible at **30 metres**. Stereo direction follows the
camera. If the native avatar harness is temporarily unavailable, audio uses the
remote climber's last network position for up to two seconds while they remain in
gameplay.

Volume is 100% from 0–5 m, fades linearly to 55% at 20 m, to 40% at 25 m,
then to silence at 30 m. Center compensation preserves the configured volume
when a speaker is directly ahead, and a final mix limiter prevents simultaneous
speakers from clipping.

Volume, stereo direction and filter parameters change smoothly on the audio thread
with a 100 ms time constant. A per-speaker low-pass filter progressively softens
distant voices: 18 kHz through 5 m, down to 6 kHz at 30 m. No echo is added.
At the range boundary, playback fades out and keeps its decoder for one second;
stepping back into range during this interval does not restart the decoder.
The local microphone test bypasses these distance effects.

There is currently no wall occlusion, elevation filter, or HRTF processing.
Proximity is an audio effect—not a confidentiality boundary. The host currently
relays voice packets to every admitted peer.

## Technical design

`VoiceFeature` declares the unreliable `voice.opus-v2` stream and delegates
capture and playback to `Game.Voice`.

| Component | Implementation |
| --- | --- |
| Audio backend | Windows shared-mode WASAPI through NAudio.Wasapi 2.2.1 |
| Capture format | 48 kHz, mono PCM |
| Codec | Opus through Concentus 2.2.2 |
| Frame duration | 20 ms |
| Target bitrate | 32 kbit/s variable bitrate; voice signal; complexity 8 |
| Maximum payload | 400 bytes |
| Pre-roll | 60 ms |
| Gate release | 250 ms |
| Reorder buffer | 60 ms |

Each packet carries burst and sequence identifiers. Receivers use a bounded PCM
queue and Opus packet-loss concealment so stalls do not accumulate stale audio.
Audio callbacks only access synchronized managed sample buffers; scene access and
codec work remain on Unity’s main thread. Device preferences store stable endpoint
IDs, while friendly names are display labels only. Playback uses Windows’ default
render device.

Release packages must include:

- `CairnMultiplayerMod.dll`;
- `CairnMultiplayerShared.dll`;
- `Concentus.dll`;
- `NAudio.Core.dll`;
- `NAudio.Wasapi.dll`;
- `THIRD-PARTY-NOTICES.txt`.

The build and packaging scripts include these files automatically.

## Verification status

### Automated coverage

Automated tests cover:

- frame validation and audible Opus round trips;
- packet loss, reordering, and sequence wrap;
- bounded backlog behavior;
- voice-gate release and pre-gain detection;
- synthetic microphone enhancement and limiting;
- exact proximity attenuation and stereo-center compensation;
- block-size-independent distance transitions, high-frequency attenuation and
  repeated crossings of the audible range boundary;
- final mixed-output limiting.

These checks do not prove device interoperability, audible quality, or real Steam
latency.

### In-game coverage

An in-game main-menu probe verified native settings integration, persistence,
microphone labels, stable device selection, Windows capture, output consumption,
local-test isolation, and capture shutdown when the page closes.

### Release checklist

- [ ] Test host → guest and guest → host with two Steam accounts.
- [ ] Test simultaneous speech and movement beyond 30 m and back.
- [ ] Change the microphone during play, then unplug and reconnect it.
- [ ] Test voice activation in a noisy room.
- [ ] Use push-to-talk while typing in chat.
- [ ] Test focus loss, pause menu, scene changes, and leave/rejoin.
- [ ] Add a third player as a late joiner.
- [ ] Listen for clipped word beginnings, clicks, and accumulated delay.
- [ ] Validate the pause-menu page separately from the main-menu page.

Both players should use the same CairnMP build; older builds do not handle this
stream.
