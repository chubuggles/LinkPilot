# LinkPilot 🌀

A small Windows app that controls the **fan and pump speeds** on a **Corsair iCUE LINK System Hub**, with no iCUE needed.
Lighting stays with **SignalRGB**. LinkPilot never touches RGB.

- Live RPM and temperatures for every LINK device
- Per-device **Fixed %** or **Curve** (drag-the-dots editor), driven by your loop's coolant sensor by default
- Window + **system tray** (left-click to open; right-click for Full blast / Exit)
- Starts with Windows (optional), runs as a normal user (no admin)
- Writes a live `status.json` and a minute-by-minute `history.csv` that scripts and AI assistants can read
- No installer and no dependencies: one `.exe` on the .NET Framework 4.8 that ships with Windows 10/11

## Quick start

1. **Fully quit iCUE.** Right-click its tray icon → Quit. Then in `services.msc`, stop **Corsair Service** and set it to *Manual*. iCUE and LinkPilot will fight over the hub if both run.
2. In **SignalRGB**, turn **off its Fan Control** feature (Settings). Keep SignalRGB for lighting only.
3. Don't run FanControl's CorsairLink plugin at the same time (it does the same job).
4. Download the zip from the [latest release](https://github.com/chubuggles/LinkPilot/releases/latest) and unzip it. Put `LinkPilot.exe` somewhere permanent (e.g. `C:\Tools\LinkPilot\`) and run it.
   *Windows SmartScreen may say "Windows protected your PC" because the exe isn't code-signed. Click **More info → Run anyway**.*
5. Your pump(s) and fans show up. Set each one to **Fixed %** or **Curve**, and you're done.

Settings and log live in `%APPDATA%\LinkPilot\` (there's a link in the app footer).

The close button follows the **Close button hides to tray** box in the footer: ticked, the window hides and LinkPilot keeps running; unticked, closing the window quits LinkPilot.
If you move `LinkPilot.exe`, just run it once from its new place and it fixes its own Start with Windows entry.

## Reading it from a script or an AI assistant

LinkPilot keeps two plain files in `%APPDATA%\LinkPilot\`. Reading them never touches the hub.

| File | What's in it |
|---|---|
| `status.json` | The state right now, rewritten every 2 seconds: coolant temperature, pump RPM, alarms, your safety settings, and for every device its port, name, RPM, temperature, speed %, mode and curve. Also the last 25 log lines. |
| `history.csv` | One line a minute: time, coolant °C, pump RPM and %, average fan RPM and %, highest fan %, and any alarm. Starts a fresh file at about 2 MB (the old one becomes `history.old.csv`). |

`status.json` has `Updated` (local time) and `Running`. If `Running` is `true` but `Updated` is more than a few seconds old, LinkPilot has stopped without a clean exit.
A clean exit writes `Running: false`.

To let an assistant such as Claude check on your loop, point it at that folder and ask, for example, "read LinkPilot's status.json and tell me how the loop is doing".

## Safety features (it's a pump, so these matter)

| What | Behaviour |
|---|---|
| Pump floor | Pumps never go below **50%** (adjustable 20–100% in the footer). Unknown LINK devices are treated as pumps too. |
| Coolant alarm | At **45 °C** coolant (adjustable), everything goes to 100% plus a tray warning, until it's 2 °C below the limit. |
| Pump stall | If a pump reads under **300 rpm** for 3 seconds, everything goes to 100% plus a warning. |
| Lost sensor | A curve whose temperature sensor disappears runs at **100%**. |
| Comms trouble | After 3 failed exchanges it reconnects automatically. The hub keeps the last speed it was given. |
| Quitting | By default it **hands the hub back to hardware mode** (its built-in behaviour). Turn that off to instead leave pump 100% / fans ≥ 60%. |
| Full blast | One click (window or tray) sends everything to 100%. Handy for bleeding the loop. |

## Playing nice with SignalRGB

Every exchange with the hub happens while holding the system-wide lock `Global\CorsairLinkReadWriteGuardMutex`.
SignalRGB and FanControl use the same lock for Corsair devices, so their messages don't get tangled with LinkPilot's.
The footer shows whether LinkPilot got the shared lock.
If SignalRGB runs elevated and LinkPilot can't open the lock, run LinkPilot as admin once to confirm that's the cause.

Note: the hub has to be in "software mode" for anyone to set fan speeds. LinkPilot switches it in, and SignalRGB already runs it that way for lighting.

## How it talks to the hub (the reverse-engineered bit)

USB HID, VID `1b1c` / PID `0c3f`, 513-byte reports. Every command is `00 00 01 <cmd…> <data…>`.

| Step | Bytes |
|---|---|
| Software mode | `01 03 00 02` (hardware mode: `01 03 00 01`) |
| Firmware | `02 13` |
| Open endpoint | `0d 01 <ep>` · close: `05 01 01 <ep>` · read: `08 01` · write: `06 01 <body>` |
| Endpoints | `36` device list · `17` RPMs · `21` temps · `18` set speeds |
| Speed body | `<len LE16> 00 00 07 00 <count> [<channel> 00 <percent> 00]…` |

Responses: byte 3 of the payload is the status (`03` means "not in software mode"), bytes 4–5 are the data type.
Speeds and temps are `<count>` × `[status, LE16 value]`, where temps are in tenths of °C.
Full details are in `src/Link/LinkProtocol.cs`.

This protocol knowledge comes from two excellent open-source projects:
[FanControl.CorsairLink](https://github.com/EvanMulawski/FanControl.CorsairLink) (Evan Mulawski) and
[OpenLinkHub](https://github.com/jurkovic-nikola/OpenLinkHub) (Nikola Jurković).
LinkPilot is an independent implementation, licensed **GPL-3.0-or-later** out of respect for theirs.
Not affiliated with Corsair.

## If something's off

- **"No iCUE LINK System Hub found"**: check the hub's USB cable. If a newer hub revision has a different product ID, the log lists every Corsair HID device it sees. Add the ID in `settings.json` → `ExtraHubProductIds` (decimal, e.g. `3135` for `0c3f`).
- **A device shows as "Unknown LINK device (0x..)"**: it still works, but it's held to the pump floor for safety. Tell me the code and I'll add it.
- **Speeds jump around or revert**: something else is also setting speeds (iCUE, SignalRGB Fan Control, FanControl).
- **Debugging**: set `"DebugPacketLog": true` in `settings.json` to log every byte sent and received.

## Building from source

The included exe was compiled with Roslyn against the .NET Framework 4.8 reference assemblies (`build/compile.sh`).
On Windows with the [.NET SDK](https://dotnet.microsoft.com/download), run `dotnet build -c Release` inside the `source` folder.

`tests/ProtocolTests.cs` checks the parsers against real hub captures, then runs a full conversation and the whole control loop against a simulated hub (64 checks).
To run it, go to `source\tests` and run `dotnet run -c Release`.
