# Changelog

## 2.0.0 - 2026-11-23 - Couch Companion (TheBadOgre)

Renamed to **Couch Companion**; based on SamsungTV-Companion 1.1.0 by leovicyto.

- **Setup app** (`Setup.cmd`): graphical install / update / uninstall, Polish or English by the Windows
  display language. `Install.cmd` / `Uninstall.cmd` are gone; `Install.ps1 -Unattended` is what the
  app runs (exit codes 0 = OK, 1 = failed, 3 = installed with problems).
- **TV off on sleep** (also from Steam): the helper switches the TV off the moment Windows announces
  sleep, over connections opened in advance, with a hard 1.5 s deadline (nothing is sent after a
  resume). Only while the TV shows the PC: not while it shows another input or a TV app.
- **IP Control** (port 1516): explicit power on/off and input switching. On resume the TV is switched
  to the PC's input (`pcInput`), also away from a set-top box or a TV app.
- **Faster, more reliable wake**: IP Control power on (~1.5 s from standby); `KEY_POWER` wakes 2023
  models from network standby; Wake-on-LAN also on UDP port 40000 (2022+ models).
- **Timer wake-ups ignored**: the TV is only turned on when a person woke the PC.
- Keep-off mode no longer switches the TV off right after a resume.
- Hardening: the helper restarts itself (task every 5 min), crash logging, sleep-clock based resume
  detection, toggle-safe key presses, log rotation, installer transcript.
- Fix: uninstalling could hang on `gpupdate` asking "OK to restart?" without a console.
- Upgrading removes a SamsungTV-Companion installation and keeps its settings.

## 1.1.0 - 2026-10-10

- **Keep off**: the tested Samsung TV turned itself back on ~30 s after being switched off with the remote,
  when Windows removes the display (HDMI hot-plug). The helper now catches that and switches the TV off
  over the network, which keeps it off. Optional (`-KeepOff`). Wake-ups done by SamsungTV-Companion itself
  (boot, resume, installer test) are marked so they are not mistaken for a self-wake.
- The installer stops a running helper before its power test.
- Watcher log records TV state changes and how long each wake-up took.
- `SamsungTVWatcher.exe --off` switches the TV off and exits.
- Key presses wait 1 s after connecting and keep the connection open a little longer (the first press
  was occasionally ignored).

## 1.0.0 - 2026-10-09

First release.

- TV turns on at boot, logon and resume (scheduled task, Wake-on-LAN).
- TV turns off at shutdown (local group policy shutdown script); stays on during restart (configurable).
- Installer wizard (English / Portuguese): discovery, pairing, power test.
- Uninstaller that preserves other shutdown scripts.
- Follows the TV by MAC address when its IP changes.
- Optional wake-on-input helper: the TV turns back on when the mouse or keyboard is used after it was switched off with the remote (~2 MB RAM, compiled from source at install).
- Power-off is confirmed and retried once (Wi-Fi occasionally loses a key press).
- CLI: `On`, `Off`, `Status`, `Pair`, `Discover`.
