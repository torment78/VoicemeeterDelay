# VoicemeeterDelay

![Voicemeeter Delay social preview](Assets/VoicemeeterDelaySocialPreview.png)

VoicemeeterDelay is a Windows control app for per-channel delay, volume trim, and routing inside Voicemeeter's audio callback path. It is built for cases where you need to line up sources, route one incoming channel to specific bus channels, or make small per-channel volume corrections without changing the whole strip or bus.

The app detects the running Voicemeeter edition, shows only the matching inputs and buses, and saves separate layouts for Standard, Banana, and Potato.

## What It Does

- **Delay**: add full-millisecond delay per selected channel.
- **Volume**: trim each selected channel from `0%` to `200%`, where `100%` is unity.
- **Routing**: route an input channel to one or more output bus channels.
- **Mute normal**: route a channel somewhere else while silencing its normal path.
- **VBAN control**: receive MacroButtons VBAN-TEXT commands for delay, volume, enable, and routing changes.
- **Round trip tester**: measure ping timing through selected Voicemeeter paths.
- **Tray mode**: minimize to the system tray and reopen, open tools, or exit from the tray menu.

## Runtime Requirements

- Windows x64
- .NET 8 Desktop Runtime
- Voicemeeter installed and running
- `VoicemeeterRemote64.dll` in the default Voicemeeter install path, beside the app, on `PATH`, or selected from the API fallback picker

Build, Visual Studio, and release-publish instructions are in [`docs/BUILD.md`](docs/BUILD.md).

## Quick Start

1. Start Voicemeeter.
2. Launch `VoicemeeterDelay.exe`.
3. Pick **Input** or **Output**.
4. Pick the strip or bus you want to edit.
5. Tick the channel you want active.
6. Set delay with the teal fader or millisecond box.
7. Set volume with the gold fader or percent box.
8. For input channels, enable **Route** to send that channel to one or more output bus channels.
9. Minimize the app if desired; it keeps running from the system tray.

Changes are live. You do not need to stop and restart the engine when ticking channels, changing faders, adjusting volume, or editing routes.

## Main Controls

**Input / Output** chooses which Voicemeeter callback side you are editing. Input covers hardware inputs and VAIO extension inputs. Output covers the Voicemeeter buses.

**I/O buttons** choose the specific strip or bus. Hardware inputs expose stereo channel controls. Virtual inputs and buses expose 8 channel controls.

**Channel tick** activates processing for that channel. If no channels or routes are enabled, the callback engine is bypassed.

**Delay** uses the teal fader and millisecond box. Values are rounded to full milliseconds. Use the mouse wheel for 1 ms steps, or hold `Shift` while scrolling for 10 ms steps.

**Volume** uses the gold fader and percent box. `100%` is unity, meaning the app follows the current Voicemeeter gain. Lower values attenuate; higher values boost.

**Route** uses the green controls on input channels. A route can send one input channel to one or more output bus channels. **Mute normal** keeps the routed audio out of the source channel's normal path while still sending it to the selected destinations.

**VBAN** uses the blue controls. Enable it when MacroButtons should control this app over VBAN-TEXT.

## Routing

Routing is available on input channels. Click **Route**, then open the route editor to add destinations. Each destination chooses an output bus and a bus channel. You can add multiple destinations from the same input channel.

Example use cases:

- Send one VAIO channel directly to `B1 Ch 3`.
- Send one hardware input channel to several output bus channels.
- Delay a routed channel while muting the original normal path.
- Keep normal processing untouched until the route checkbox is enabled.

## Round Trip Tester

The **Round Trip** window can send a ping through selected Voicemeeter input and return paths. It can run single pings or multi-ping calibration passes and prints raw timing results that can be copied or saved.

This is useful for checking how much delay a Voicemeeter route or callback configuration actually adds on your system. Voicemeeter latency can vary by buffer state, so repeated measurements are more useful than a single number.

## Saved Settings

The app saves:

- selected input/output side
- selected I/O endpoint
- enabled channel ticks
- delay and volume values
- route destinations
- mute-normal states
- VBAN listener settings
- API DLL fallback path

Profiles are stored separately for Standard, Banana, and Potato, so Potato-only channels do not get carried into Banana or Standard.

## MacroButtons VBAN Control

Enable **VBAN** in the main window to let MacroButtons send VBAN-TEXT commands into this app. For a same-PC setup, keep **Local only** enabled, set the app port to `6981`, and configure a MacroButtons VBAN-TEXT output slot such as `vban1` to send to `127.0.0.1`, port `6981`, with the same stream name shown in the app, for example `Command1` or `VDControl`.

For the full command reference, open [`VBAN_COMMANDS.md`](VBAN_COMMANDS.md) or use the **VBAN Commands** button inside the app. Release ZIP downloads include both `README.md` and `VBAN_COMMANDS.md`.

`vban1` is the MacroButtons output slot name. The stream name is configured inside that slot and must match the app's **Stream** field.

Commands follow Voicemeeter-style zero-based `Strip` / `Bus` syntax, namespaced with `VD.`:

```text
SendText("vban1", VD.Strip(0).Ch(1).Enable=1;);
SendText("vban1", VD.Strip(5).Ch(All).Delay=35;);
SendText("vban1", VD.Strip(6).Ch(1-2).Volume=90;);
SendText("vban1", VD.Bus(B1).Ch(1).Enable=1; VD.Bus(B1).Ch(1).Delay=25;);
SendText("vban1", VD.Strip(0).Ch(1).Route=Bus(B1).Ch(3););
SendText("vban1", VD.Strip(0).Ch(1).Route+=Bus(B2).Ch(4););
SendText("vban1", VD.Strip(0).Ch(1).MuteNormal=1;);
```

For Potato input strips, `Strip(0-4)` are Hardware In 1-5, `Strip(5)` is VAIO, `Strip(6)` is AUX, and `Strip(7)` is VAIO3. Bus labels such as `A1`, `A2`, `B1`, `B2`, and `B3` can be used directly.

For route commands, `Route=Bus(B1).Ch(3)` replaces the source channel's route list and enables routing, `Route+=Bus(B2).Ch(4)` adds another destination, and `Route-=Bus(B1).Ch(3)` removes that destination. `RouteEnable=0` disables routing without deleting saved destinations. `MuteNormal=1` silences the source channel's normal path while the route is active.

## Notes

- `Output` delays Voicemeeter bus/output insert channels before the master section.
- `Input` delays input insert channels before strip processing.
- `Main` is exposed for experimentation, but output/input insert modes are usually the practical choices for a pure delay.
- Pick `Input` or `Output`, then choose one strip/bus button.
- The header shows the detected running mixer edition: Standard, Banana, or Potato.
- Hardware inputs expose `L` and `R` channel strips.
- Virtual inputs and output buses expose 8 vertical channel strips.
- Each channel strip has its own enable checkbox, vertical delay slider, millisecond value, and volume percent control.
- Channel volume is relative to Voicemeeter's current level: `100%` is unity/no change, lower values attenuate, and higher values boost.
- Volume-only processing does not allocate an extra delay buffer, but the matching input/output callback side still has to be armed.
- Input channel strips also have optional routing controls. A route can send that input channel into one or more output bus channels, and can mute the source channel's normal path. Route processing is inactive unless a route checkbox is enabled.
- **Input floor** and **Output floor** are kept at `0 ms` by default so the faders show only the delay amount configured by the user.
- The toolbar floor box follows the selected side: open Input to edit the input floor, or Output to edit the output floor.
- Channel faders show total added delay: path floor plus any extra delay-line time. A checked fader at the bottom arms the callback path, but adds `0 ms` extra delay line.
- Use the mouse wheel on a slider to nudge it by 1 ms, or hold `Shift` while scrolling to nudge by 10 ms.
- Starting the app applies every configured per-channel input and output delay at the same time.
- Faders, volumes, channel enables, and input/output selections remain editable while running; changes are pushed live to the callback engine.
- Faders, volumes, enabled channel ticks, selected input/output, DLL fallback path, and hidden floor values are saved automatically and restored on the next launch. The engine waits for a tick/edit or command before arming, so Release startup stays conservative.
- Channel, route, delay, volume, and selected I/O state is saved in separate Standard, Banana, and Potato profiles. When the app detects a different running mixer edition, it switches to that edition's profile instead of carrying Potato-only strips/buses into Banana or Standard.
- The app only arms sides that have at least one enabled channel. While running, ticking a channel on a new side re-arms that side; unticking the last channel on a side removes that side.
- The API DLL fallback path is locked while running and requires stopping before changing it.
- The delay path writes delayed samples only; it is not an echo effect and does not add feedback.
- During delay-line priming, audio passes through until the buffer fills.
