# Discord Summary

Copy/paste this into Discord:

```text
VoicemeeterDelay

Per-channel delay, routing, and volume control for Voicemeeter.

What it does:
- Adds full-millisecond delay per selected input or output channel
- Adds per-channel volume trim from 0% to 200%, with 100% as unity
- Routes input channels directly to one or more Voicemeeter bus channels
- Can mute the normal source path while routing the channel somewhere else
- Supports hardware inputs, VAIO inputs, AUX, VAIO3, and output buses depending on the running Voicemeeter edition
- Detects Standard, Banana, or Potato and keeps separate saved profiles for each
- Lets you change delay, volume, channel ticks, and routes live while the engine is running
- Includes a round trip tester for measuring real latency through selected Voicemeeter paths
- Runs in the system tray with quick actions for open, tools, settings, and exit
- Can be controlled from MacroButtons with VBAN-TEXT commands

Useful for:
- Lining up microphones, virtual inputs, and bus outputs
- Delaying only one channel instead of a whole strip or bus
- Sending one input channel to a specific bus/channel destination
- Making small volume corrections without changing the main Voicemeeter gain
- Testing how much timing delay a route or callback setup actually adds

Download:
https://github.com/torment78/VoicemeeterDelay/releases/tag/v1.0.0

GitHub:
https://github.com/torment78/VoicemeeterDelay

VBAN command reference:
https://github.com/torment78/VoicemeeterDelay/blob/master/VBAN_COMMANDS.md
```
