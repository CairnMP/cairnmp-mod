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
| Push-to-talk key | `V` | Configurable from the settings dropdown. |
| Voice volume | 100% | Adjustable from 0% to 200%. |
| Maximum audible distance | 30 m | Volume decreases with distance. |

## Using voice chat

### Voice modes

- **Voice activation** opens the microphone when the input level crosses the
  configured threshold.
- **Push-to-talk** transmits only while the selected key is held.
- **Microphone mute** stops outgoing voice without muting other players.

The first implementation uses an RMS level gate. It does not provide speech
classification, echo cancellation, or noise suppression.

### Choosing a microphone

Select the Windows communications default or a named input device. Devices are
refreshed every five seconds on a worker thread.

> [!NOTE]
> If a selected device is unplugged, CairnMP preserves the selection and stops
> capture. It never switches silently to another microphone.

### Testing and muting

- **Test microphone** plays the selected input locally. Nothing is transmitted.
  Closing the page stops the test; headphones are recommended to avoid feedback.
- **Mute a player** temporarily silences an individual player listed when the
  page opens. These mutes clear after a scene or session reset, or when the player
  leaves.

Preferences are stored under `CairnMP.Voice`. Normal capture stops outside
gameplay, when muted, after focus loss, on scene reset, and on disconnect. The
explicit local test may capture while the settings page is open, but network
transmission remains suspended there.

## Spatial audio behavior

Remote voices become inaudible at **30 metres**. Stereo direction follows the
camera, and audio is played only when the remote climber has an available in-game
avatar.

There is currently no wall occlusion, elevation filter, or HRTF processing.
Proximity is an audio effect—not a confidentiality boundary. The host currently
relays voice packets to every admitted peer.

## Technical design

`VoiceFeature` declares the unreliable `voice.opus-v1` stream and delegates
capture and playback to `Game.Voice`.

| Component | Implementation |
| --- | --- |
| Audio backend | Windows shared-mode WASAPI through NAudio.Wasapi 2.2.1 |
| Capture format | 24 kHz, mono PCM |
| Codec | Opus through Concentus 2.2.2 |
| Frame duration | 20 ms |
| Target bitrate | 24 kbit/s |
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
- voice-gate release.

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
