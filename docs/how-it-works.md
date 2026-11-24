# How it works

Notes for anyone who wants to understand, port or extend Couch Companion.

The first part covers what Couch Companion added, found on a **QE55Q80C (2023)** over a cable. The
second part is the original SamsungTV-Companion notes by leovicyto, found on a **QN55Q70T (2020,
Tizen 5.5)**; file and task names there are the old ones.

## Couch Companion additions (QE55Q80C, 2023)

### Waking the TV
- **Wake-on-LAN only on UDP 40000.** Packets on the classic ports 9 and 7 were ignored.
- **`KEY_POWER` wakes it from network standby** (unlike the 2020 model), within ~1 s - except in the
  first ~1-2 min after it was switched off, when it ignores both keys and magic packets.
- **IP Control `powerControl` / `powerOn`** works from network standby in ~1.5 s and is explicit, so
  repeating it is harmless. It is the first choice; `KEY_POWER` is only sent if IP Control did not
  answer (a toggle arriving while the TV is starting from "powerOn" switches it straight back off).

### IP Control (HTTPS JSON-RPC, port 1516, "IP Remote" on the TV)
- Pairing: `{"jsonrpc":"2.0","id":"1","method":"createAccessToken"}` shows a prompt on the TV and
  returns `result.AccessToken`. Every other call carries `params.AccessToken`.
- **Headers and body in one write, and no `Connection: close` header** - otherwise the TV answers
  `{"code": -32700, "message": "Parse error"}` to a valid request. The same "Parse error" is the
  answer to a missing or rejected token. The TV keeps **one** token: pairing again invalidates it.
- `inputSourceControl` reads (`{"inputSource":"HDMI1"}`) and sets the input; `getTVStates` reads it
  too. `KEY_HDMI1..4` over the remote channel are ignored on this model, and `KEY_HDMI` only cycles.
- One request per connection, but an idle TLS connection is kept open for at least 60 s - so a
  connection can be opened in advance and only the request written when needed.
- **While an app (YouTube, Netflix...) is on screen, `inputSource` still reports the last HDMI
  input.** The TV cannot list the foreground app, but `GET :8001/api/v2/applications/<id>` tells
  whether one app is `visible`; known app ids are checked. Setting the input explicitly leaves the app.

### TV off when the PC sleeps
- The helper gets `WM_POWERBROADCAST / PBT_APMSUSPEND` (a hidden top-level window: message-only windows
  do not receive broadcasts). After a sleep from Steam (`SetSuspendState`) Windows suspends ~1 s later.
- Opening a connection then is a race, and a request still in flight finishes **after the resume** -
  a late `KEY_POWER` once switched the TV off right after a wake. So: state, input and visible app are
  measured every 3 s beforehand, an IP Control connection is kept armed (renewed every 30 s), and the
  handler only writes "powerOff" on it; nothing is sent after a 1.5 s deadline or once a resume began.
- Remote-channel fallback: one WebSocket kept open with a heartbeat. On .NET Framework a timed-out
  `SslStream` read loses data (the stream goes out of sync), so frames are read without a timeout and a
  separate heartbeat closes a dead connection.

### Resume
- `PBT_APMRESUMEAUTOMATIC` comes with every resume, including timer wake-ups (Windows Update, "ACPI Wake
  Alarm"); `PBT_APMRESUMESUSPEND` only when a person woke the PC. The TV is only turned on for the
  latter (or at the first keyboard/mouse input after an automatic resume).
- While the PC sleeps its own network is down, so the TV reads "offline"; keep-off mode must not take
  the TV "coming back" after the resume for a self-wake.
- Missed broadcasts are caught with a sleep clock: `GetTickCount64` counts sleep,
  `QueryUnbiasedInterruptTime` does not - their difference grows only while the PC sleeps.

### Windows cannot tell which input the TV shows
Switching the TV to another input changes nothing Windows can see (display stays active, no PnP or
audio endpoint change), hence IP Control.

### Uninstall from the setup app
`gpupdate /force` can ask "OK to restart? (Y/N)"; without a console it waits forever. It now gets "N"
on its input and `/wait:60`, and the app closes the scripts' standard input.

---

# Original notes (SamsungTV-Companion, leovicyto)

Notes for anyone who wants to understand, port or extend SamsungTV-Companion. Most of this was found
the hard way on a QN55Q70T (2020, Tizen 5.5), first over Wi-Fi and then over a cable.

## The TV's network interfaces

| Port | What | Used for |
|---|---|---|
| `8001` HTTP | `GET /api/v2/` returns device info as JSON | discovery, `device.PowerState` (`on` / `standby`), MAC (`wifiMac`), model |
| `8001` WS | `ws://…/api/v2/channels/samsung.remote.control` | **rejected** on 2018+ models (`ms.channel.unauthorized`) |
| `8002` WSS | `wss://…/api/v2/channels/samsung.remote.control?name=<base64>&token=<token>` | remote keys (`KEY_POWER`). Self-signed certificate |

Pairing: the first connection to `8002` makes the TV show an *Allow / Deny* prompt (if *Access
Notification* is not *Off*). On *Allow*, the first message is

```json
{"event":"ms.channel.connect","data":{"id":"…","token":"12345678", …}}
```

Sending the token on later connections skips the prompt. A key press is:

```json
{"method":"ms.remote.control","params":{"Cmd":"Click","DataOfCmd":"KEY_POWER","Option":"false","TypeOfRemote":"SendRemoteKey"}}
```

## Pitfall 1: .NET's ClientWebSocket loses the TV's first message

`System.Net.WebSockets.ClientWebSocket` (.NET Framework 4.x, the one Windows PowerShell 5.1 uses)
connects fine, reports `State = Open`, and then **never receives anything**. Looking at the raw TCP
stream shows why: the TV sends the `HTTP/1.1 101 Switching Protocols` response **and** the first
WebSocket frame in the same TCP segment, and as far as we can tell the framework's handshake reader
consumes and drops the bytes after the headers.

So the connection looks "silent" and the pairing seems to hang, as if the TV were waiting for the user to
accept a prompt that never shows up. Most people end up blaming the TV.

The fix is a minimal RFC 6455 client over `SslStream`: read the HTTP headers **byte by byte** until
`\r\n\r\n`, then parse frames yourself (server frames are unmasked; client frames must be masked).
That is about 60 lines in `SamsungTV.Core.ps1`.

## Pitfall 2: you cannot turn the TV on with KEY_POWER

With *Power On with Mobile* enabled, a TV in standby keeps Wi-Fi up and still answers
`GET :8001/api/v2/` with `"PowerState": "standby"`. It is tempting to send `KEY_POWER` again. But
in standby the TV **accepts the WebSocket connection and never answers**, so the command goes nowhere.

What works is **Wake-on-LAN**: the standard magic packet (6×`FF` + 16× MAC) to UDP 9/7. It is sent to
`255.255.255.255`, to the subnet broadcast and to the TV's last IP, and **repeated every 2 seconds**.
A single packet right after the TV entered standby was not enough in testing. Repeating it turned the
TV on in about 9 s.

Use the MAC of the interface the TV is actually using. Despite its name, `device.wifiMac` reported the
**wired** MAC when the TV was on a cable. The installer reads the MAC from the ARP table first anyway.
Wi-Fi and cable have different MACs, so moving the TV between them needs a re-install.

## What the TV does after power-off

Measured over a cable, polling every 2 s:

| Time after `KEY_POWER` | `GET :8001/api/v2/` | ping |
|---|---|---|
| 0–3 s | TV closes the WebSocket itself | yes |
| ~3–20 s | `"PowerState": "standby"` | yes |
| ~20 s on | no answer at all | no |
| Wake-on-LAN sent at 45 s | `"PowerState": "on"` 2 s later | yes |

So "off" means the TV disappears from the network completely, but its NIC still listens for the magic
packet. Over Wi-Fi the TV also reconnected later in a `standby` state.

## Wi-Fi vs cable

Over Wi-Fi each direction failed once in a handful of tests: one power-off key was accepted but ignored,
and one 90-second wake attempt got no reaction, while an identical attempt a few minutes earlier woke the
TV in 9 s. Over a cable every test worked and wake-up took ~2 s. That is why `Invoke-TvOff` confirms the
state and retries once. It waits 6 s before retrying, because `KEY_POWER` toggles: a second press while the
TV is already shutting down would turn it back on.

## Pitfall 3: running something at shutdown, reliably

A scheduled task triggered by the shutdown event (`User32` 1074) races against Windows stopping
services and the network. Sometimes it runs, sometimes it doesn't.

A **machine shutdown script** in the local group policy does not have that problem: Windows runs it
before tearing down the network, and waits for it (up to the policy timeout). It is registered by:

- `%windir%\System32\GroupPolicy\Machine\Scripts\scripts.ini` (UTF-16, hidden):
  ```ini
  [Shutdown]
  0CmdLine=C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe
  0Parameters=-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "...\SamsungTV.ps1" Off -OnShutdown
  ```
- `%windir%\System32\GroupPolicy\gpt.ini`: add the Scripts client-side extension
  `[{42B5FAAE-6536-11D2-AE5A-0000F87571E3}{40B6664F-4972-11D1-A7CA-0000F87571E3}]` to
  `gPCMachineExtensionNames` and increment `Version`;
- then `gpupdate /target:computer /force`.

The installer **merges** its entry with any existing ones and the uninstaller removes only its own.

## Telling a restart from a shutdown

Inside the shutdown script there is no API that says "this is a restart". But Windows logs **event
1074 (User32)** when a shutdown starts, before the scripts run, and its 5th property is the type:
`restart` / `power off` in English, `reiniciar` / `Desligado` in Portuguese, `Neustart` in German, and
so on. It is localized, so `Test-RestartInProgress` matches the restart word in the common languages.
Anything unknown is treated as a shutdown, so in the worst case the TV turns off, which is the
behaviour you would have had anyway.

## The TV turns itself back on after the remote

Recording TV state and the Windows display device once per second while the TV was switched off:

| | Switched off with the **remote** | Switched off over the **network** (`KEY_POWER`) |
|---|---|---|
| 0–20 s | `standby`, then offline | `standby`, then offline |
| 22–35 s | Windows logs Kernel-PnP **1010**: `DISPLAY\SAM7003` removed, "absent from the bus" | display stays present |
| < 1 s later | **TV is on again** | TV stays off (90 s recorded, then woken on purpose) |

So, switched off with the remote, the TV drops the HDMI hot-plug line; Windows and the graphics driver
(AMD here) react to the display disappearing, and that activity wakes the TV. Disabling *Anynet+
(HDMI-CEC)* made no difference, and nothing else was connected to the TV. It happened in 2 of 3
remote power-offs. In the third, Windows never removed the display and the TV stayed off.

The helper's *keep off* mode polls every 2 s for 90 s after the TV goes off. If the TV comes back on and
`GetLastInputInfo` has not changed since it went off (nobody touched the PC), it sends `KEY_POWER` over
the network. That puts the TV in the standby that does not drop the hot-plug line.

Wake-ups done on purpose by SamsungTV-Companion (boot, logon, resume, the installer's power test) touch
`last-wake.txt` next to `config.json`, and the helper ignores a wake-up within 2 minutes of that file being
updated. Without that, resuming from sleep would look like the TV waking up by itself (found the hard way:
the installer's power test got switched off by a running helper). The remaining trade-off: turning the TV
on with the remote within that window, without touching the PC, looks the same, so it gets switched off.

A key sent immediately after `ms.channel.connect` was sometimes ignored: one network power-off took
17 s because the first press was lost and was retried. Waiting 1 s before sending, and keeping the
connection open 1.5 s afterwards, brought it to 5 s.

## Wake on input (optional helper)

Windows has no "mouse moved" trigger for scheduled tasks, so this part needs something running.
`src/Watcher/SamsungTVWatcher.cs` is a ~200-line C# program compiled at install time by the
`csc.exe` that ships with .NET Framework 4.x (so it is C# 5). It runs in the user's session, started by a
logon task for the Users group, and:

- polls the TV state every 10 s (plain HTTP/1.0 over a socket);
- reads `GetLastInputInfo` every 0.5 s;
- if there was new input and the TV has been off for at least `wakeOnInputGraceSeconds` (15 s), sends
  Wake-on-LAN every 2 s until the TV reports `on` (max 60 s, then pauses 5 min).

The grace period is there so that switching the TV off with the remote while your hand is still on the
mouse does not turn it straight back on.

Memory: a first version using `HttpWebRequest` had a 23 MB working set. Replacing it with a raw socket and
calling `SetProcessWorkingSetSize(-1, -1)` after each check brought the working set to about 2 MB
(private commit stays around 17 MB, mostly untouched and pageable). CPU time is about 0.05 s per minute.

## Finding the TV again

The config stores IP and MAC. If the TV does not answer at its IP, every host on the local /24 is
probed on TCP 8001 in parallel (about 2 s), and the one whose MAC matches is the TV. The new IP is
saved.
