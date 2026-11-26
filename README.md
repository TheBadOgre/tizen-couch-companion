# Couch Companion

*Vibecoded with love* :heart:

Your **Samsung Tizen TV follows your Windows PC** - made for a PC used as a living-room console
(Steam Big Picture, couch gaming). PC graphics cards have no HDMI-CEC; Couch Companion does it over
your home network. No cloud, no account, no extra hardware.

[Polski](README.pl.md)

| The PC... | The TV... |
|---|---|
| goes to sleep (also from Steam) | turns **off** - only while it shows the PC |
| is woken by you | turns **on** and switches **to the PC's input** (also away from a set-top box or a TV app) |
| wakes by itself (Windows Update) | stays as it is |
| shuts down / restarts | turns off / stays on (your choice) |

Optional: turn the TV on with the mouse or keyboard; switch it off again if it turns itself back on
after the remote.

## Install

1. On the TV: *Settings > General > Network > Expert Settings* - **Power On with Mobile** and
   **IP Remote** on. Same network as the PC; a cable is best.
   Also turn **Start with Smart Hub Home** off (*Settings > General & Privacy* or *Advanced Features >
   Start Screen Option*; older TVs: *General > Smart Features > Autorun Smart Hub*), so the TV does
   not always start on the Home screen.
2. Double-click **`Setup.cmd`** - it builds the setup app with the compiler that ships with Windows
   and starts it (admin rights needed).
3. **Find** the TV, pick the PC's input, **Install**, and accept the prompts on the TV.

The same app updates and uninstalls. Polish or English, by the Windows language.

## Supported TVs

Windows 10/11 and a Samsung Tizen TV. What works depends on which of the TV's network APIs the model has:

| TV year | Tizen | Remote channel (wss :8002) | IP Control (https :1516) | Result |
|---|---|---|---|---|
| 2023+ | 7.0+ | yes | yes | **everything** - tested on QE55Q80C (2023, Tizen 7.0) |
| 2020-2022 | 5.5-6.5 | yes | yes | everything (2020 / Tizen 5.5 tested by the original project) |
| 2018-2019 | 4.0-5.0 | yes | no (port 1515, not used) | on/off only: no input switching, the TV is switched off on sleep whatever it shows |
| 2016-2017 | 2.4-3.0 | no (8001 only) | no | not supported |

IP Control needs *IP Remote* enabled on the TV. Some models leave parts of it out (e.g. input
switching on The Frame 2024); then the TV still follows the PC, only the input stays as it is.

## How it works

IP Control (HTTPS, port 1516) for explicit power on/off and input switching, the remote-control
channel (port 8002) and Wake-on-LAN as fallbacks, and a ~2 MB background helper that turns the TV off
the instant Windows announces sleep. Details: [docs/how-it-works.md](docs/how-it-works.md).

Logs: `C:\ProgramData\CouchCompanion` and `%LOCALAPPDATA%\CouchCompanion`.

## Credits

**Couch Companion** by **TheBadOgre** - vibecoded with love, with an AI pair programmer.
Based on [SamsungTV-Companion](https://github.com/leovicyto/SamsungTV-Companion) by **leovicyto**.
MIT License.
