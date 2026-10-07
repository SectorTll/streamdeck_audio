# Audio Keys & HA Keys — Stream Deck plugins

Two small, event-driven Stream Deck plugins for Windows, written in C# (.NET 8):

- **Audio Keys** — pick the default output or input device, control volume, mute, play/pause.
- **HA Keys** — toggle Home Assistant entities and see their state.

Every key has the same visual language: a glyph in the middle and a **status LED bar** at the top that
looks like a real LED behind frosted glass — bright core, dark ends, no outer glow, no blinking.
Three states, three configurable colours.

```
 ┌──────────────┐  ┌──────────────┐  ┌──────────────┐
 │  ▬▬▬▬▬▬▬▬▬▬  │  │  ▪▪▪▪▪▪▪▪▪▪  │  │  ▬▬▬▬▬▬▬▬▬▬  │   green  = selected / on
 │      🎧      │  │      🎧      │  │      🎧      │   dim    = present / off
 │              │  │              │  │              │   yellow = absent / unavailable
 └──────────────┘  └──────────────┘  └──────────────┘
```

No polling anywhere: the plugins sit on Windows Core Audio notifications, the WinRT media-session
API and a single Home Assistant WebSocket. CPU at idle is zero; a key is re-rendered only when its
state actually changes, and rendered images are cached as ready-to-send data URIs.

## Actions

### Audio Keys

| Action | What it does | LED |
|---|---|---|
| **Output Device** | makes the chosen playback device the default (optionally also for communications) | selected / present / absent |
| **Input Device** | same for microphones | selected / present / absent |
| **Volume Up / Down** | nudges the default output volume by a step | level meter, red when muted |
| **Volume Set** | sets a level, optionally fading to it | level meter |
| **Mute** | toggles mute on the default output | red while muted, green while on |
| **Play / Pause** | toggles the current media session (falls back to the media key) | lit while playing |

Device keys are bound by endpoint ID and fall back to the device name, so they survive Windows
re-enumerating a device after a driver reinstall. An absent device can show a colour of your choice
or hide the key entirely.

### HA Keys

| Action | What it does | LED |
|---|---|---|
| **Entity** | calls `toggle` / `turn_on` / `turn_off` on a Home Assistant entity | on / off / unavailable |

The entity list in the inspector comes live from Home Assistant. One WebSocket connection serves all
keys; it reconnects with back-off and after the PC wakes up.

## Requirements

- Windows 10/11, Stream Deck software 6.4 or newer (tested with 7.6)
- .NET 8 runtime with Windows Desktop (`Microsoft.WindowsDesktop.App 8.x`) — present on most machines
  that have any .NET 8 desktop app installed
- To build: .NET 8 SDK. A user-local install via `dotnet-install.ps1 -Channel 8.0` is enough
  (`build.ps1` picks it up from `%USERPROFILE%\.dotnet` automatically)

## Build & install

```powershell
.\build.ps1              # publish both plugins, generate icons, install, restart Stream Deck
.\build.ps1 -NoRestart   # install without restarting
.\build.ps1 -Only ha     # one plugin only (audio | ha)
```

The script copies each plugin to `%APPDATA%\Elgato\StreamDeck\Plugins\<uuid>.sdPlugin`. The plugins
are side-loaded, so Stream Deck's Marketplace tamper check does not apply and you are free to
change anything.

Stream Deck is restarted through a one-shot scheduled task so that it runs with your real logon
token, not as a child of the shell that ran the script (this matters if that shell is sandboxed).

## Setting up HA Keys

1. In Home Assistant open your profile → **Security** → **Long-lived access tokens** → create one.
2. Drop an **Entity** key on your deck, open its settings, enter the Home Assistant URL
   (`http://host:8123`) and paste the token. These two fields are shared by all HA keys.
3. Pick the entity, the service to call on press, a glyph and the three LED colours.

The plugin keeps its own DPAPI-encrypted copy of the URL and token in `config\hakeys.cfg` inside the
plugin folder and connects from it at start. That copy exists because Stream Deck 7.6 was seen losing
plugin global settings after a reboot (*"Failed to parse account settings from credentials"* in its
log); when that happens the plugin re-seeds Stream Deck's store from its copy.

## Glyphs

Glyphs are drawn in code (headphones, speakers, monitor, VR, headset, mic, USB, Bluetooth, plug, lamp,
fan, power, …), so they render at any size and colour without image files.

Any PNG dropped into `imgs/glyphs/<id>.png` overrides the drawn glyph with that id. To turn an
arbitrary icon into a glyph — black-on-white, white-on-black, transparent, even a dark tile with the
strokes cut out — use the importer; it detects the ink, fits it into the glyph box and writes a
144×144 light-grey-on-transparent PNG:

```powershell
HAKeys.exe --import-glyph "C:\icons\floor-light.png" "plugin-ha\imgs\glyphs\floor.png"
```

`AudioKeys.exe --preview sheet.png [glyphDir]` renders every glyph in every state into one sheet for
eyeballing.

## Troubleshooting

- Plugin logs: `<plugin folder>\logs\audiokeys.log` and `hakeys.log`. Every key appearance, press,
  device/volume change and HA (re)connection is logged on one line.
- `AudioKeys.exe --selftest` switches the default device to another active one and back, nudges the
  volume and checks that every notification arrived — without Stream Deck.
- `HAKeys.exe --selftest <url> <token>` connects to Home Assistant and lists a few entities.
- `tools\sd-hang-watch.ps1` watches `StreamDeck.exe` and writes a full minidump (built-in
  `comsvcs MiniDump`, no extra tools) if the app stops responding for 10 s — handy when chasing hangs
  in the Stream Deck application itself.

## Repository layout

```
src/Shared/      logger, Stream Deck WebSocket client, renderer + glyphs, glyph importer, settings view
src/AudioKeys/   Core Audio interop (IMMDevice*, IPolicyConfig, IAudioEndpointVolume), volume & media monitors
src/HAKeys/      Home Assistant WebSocket client, entity action, encrypted config store
plugin-audio/    manifest, property inspectors (sdpi-components v4), icons (generated)
plugin-ha/       manifest, property inspector, icons, PNG glyph overrides
tools/           Stream Deck hang watcher
build.ps1        build + install both plugins
```

## Notes on the design

- **Why C#?** Switching audio devices and following their state needs COM (Core Audio). Doing that
  in-process avoids a helper executable and lets the plugin react to `IMMNotificationClient` /
  `IAudioEndpointVolumeCallback` events instead of polling.
- **Never call Core Audio from its own callbacks** — that deadlocks. Callbacks only hand the event to
  the thread pool; a timed lock guards the volume endpoint so a stuck COM call can never hang the key
  handler.
- **One image per state.** Images are cached by `(glyph, colour, state, fill, label)`; a repeated state
  is a dictionary lookup, and nothing is sent to Stream Deck unless the picture actually changed.
