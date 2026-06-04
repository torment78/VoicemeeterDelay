# Voicemeeter Delay VBAN Command Examples

These are MacroButtons `SendText` examples for the app's VBAN-TEXT control.

`vban1` is the MacroButtons VBAN-TEXT output slot. The slot's stream name and UDP port must match the app's VBAN settings.

## Basics

`Strip(...)` and `Bus(...)` are zero-based where numbers are used.

`.Ch(...)` is one-based:

```text
Ch(1)
Ch(1-2)
Ch(All)
```

For Potato input strips:

```text
Strip(0) = Hardware In 1
Strip(1) = Hardware In 2
Strip(2) = Hardware In 3
Strip(3) = Hardware In 4
Strip(4) = Hardware In 5
Strip(5) = VAIO
Strip(6) = AUX
Strip(7) = VAIO3
```

Bus labels can be used directly:

```text
Bus(A1)
Bus(A2)
Bus(A3)
Bus(A4)
Bus(A5)
Bus(B1)
Bus(B2)
Bus(B3)
```

## Input Strip Commands

Enable or disable processing on a source channel:

```text
SendText("vban1", VD.Strip(0).Ch(1).Enable=1;);
SendText("vban1", VD.Strip(0).Ch(1).Enable=0;);
```

Set delay:

```text
SendText("vban1", VD.Strip(0).Ch(1).Delay=25;);
```

Add delay:

```text
SendText("vban1", VD.Strip(0).Ch(1).Delay+=10;);
```

Subtract delay:

```text
SendText("vban1", VD.Strip(0).Ch(1).Delay-=10;);
```

Set volume:

```text
SendText("vban1", VD.Strip(0).Ch(1).Volume=100;);
```

Add volume:

```text
SendText("vban1", VD.Strip(0).Ch(1).Volume+=5;);
```

Subtract volume:

```text
SendText("vban1", VD.Strip(0).Ch(1).Volume-=5;);
```

## Output Bus Commands

Enable or disable processing on an output channel:

```text
SendText("vban1", VD.Bus(B1).Ch(1).Enable=1;);
SendText("vban1", VD.Bus(B1).Ch(1).Enable=0;);
```

Set delay:

```text
SendText("vban1", VD.Bus(B1).Ch(1).Delay=25;);
```

Add delay:

```text
SendText("vban1", VD.Bus(B1).Ch(1).Delay+=10;);
```

Subtract delay:

```text
SendText("vban1", VD.Bus(B1).Ch(1).Delay-=10;);
```

Set volume:

```text
SendText("vban1", VD.Bus(B1).Ch(1).Volume=100;);
```

Add volume:

```text
SendText("vban1", VD.Bus(B1).Ch(1).Volume+=5;);
```

Subtract volume:

```text
SendText("vban1", VD.Bus(B1).Ch(1).Volume-=5;);
```

## Route Commands

Replace the route list for strip 0 channel 1 and route it to bus B1 channel 3:

```text
SendText("vban1", VD.Strip(0).Ch(1).Route=Bus(B1).Ch(3););
```

Add another route destination:

```text
SendText("vban1", VD.Strip(0).Ch(1).Route+=Bus(B2).Ch(4););
```

Remove one route destination:

```text
SendText("vban1", VD.Strip(0).Ch(1).Route-=Bus(B1).Ch(3););
```

Enable saved routes:

```text
SendText("vban1", VD.Strip(0).Ch(1).RouteEnable=1;);
```

Disable saved routes without deleting them:

```text
SendText("vban1", VD.Strip(0).Ch(1).RouteEnable=0;);
```

Mute the normal source path while routing:

```text
SendText("vban1", VD.Strip(0).Ch(1).MuteNormal=1;);
```

Restore the normal source path:

```text
SendText("vban1", VD.Strip(0).Ch(1).MuteNormal=0;);
```

## Combined Commands

Multiple commands can be sent in one `SendText`:

```text
SendText("vban1", VD.Strip(0).Ch(1).Route=Bus(B1).Ch(3); VD.Strip(0).Ch(1).MuteNormal=1; VD.Strip(0).Ch(1).Delay=20;);
```

## Accepted Aliases

Enable:

```text
Enable
Enabled
```

Delay:

```text
Delay
DelayMs
Ms
```

Volume:

```text
Volume
Vol
Gain
```

Route enable:

```text
RouteEnable
RouteEnabled
```

Mute normal:

```text
MuteNormal
RouteMute
MuteRoute
RouteMuteNormal
```

Boolean values:

```text
1, 0
true, false
on, off
yes, no
```
