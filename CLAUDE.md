# Couch Companion - notes for AI agents

`AGENTS.md` is a symlink to this file: one source of truth for Claude Code, Codex and other agents.

## Keep this file up to date

**Any change that makes something here wrong or incomplete updates this file in the same commit.**
That includes adding, renaming or removing files, build steps, scripts, parameters, config keys,
install locations, scheduled tasks or release steps. Before finishing a task, re-read the sections
you touched and fix them. Keep it short: facts an agent needs, not a copy of the README.

Also keep in step:
- `CHANGELOG.md` - one entry per user-visible change (newest on top, `## x.y.z - YYYY-MM-DD`).
- `README.md` **and** `README.pl.md` - user-facing docs exist in English and Polish; change both.
- `docs/how-it-works.md` - new TV/Windows behaviour you found (what, on which model, how measured).
- UI text lives in `src/Gui/Strings.cs` in both languages; never add a string in only one.

## What it is

Windows tool that makes a Samsung Tizen TV follow a PC used as a living-room console: the TV turns
off when the PC sleeps / shuts down (only while it shows the PC), and on + to the PC's HDMI input when
a person wakes the PC. Everything goes over the home network (no HDMI-CEC on PC GPUs). Forked from
SamsungTV-Companion 1.1.0 (leovicyto); current version is in `CHANGELOG.md`. MIT.

TV interfaces used (details and pitfalls in `docs/how-it-works.md` - read it before touching TV code):
- **IP Control** - HTTPS JSON-RPC on 1516 ("IP Remote" on the TV): explicit power on/off, input.
- **Remote channel** - WSS on 8002: `KEY_POWER`; hand-written RFC 6455 client (not ClientWebSocket).
- **HTTP 8001** `/api/v2/` - discovery, power state, MAC, visible app.
- **Wake-on-LAN** - UDP 9/7 and 40000.

## Layout

| Path | What |
|---|---|
| `Setup.cmd` | The only entry point: compiles the setup app into `build\` and starts it |
| `src/Gui/*.cs`, `MainWindow.xaml`, `app.manifest` | WPF setup app (install / update / uninstall), runs the PS scripts |
| `src/Install.ps1`, `Uninstall.ps1` | Installer / uninstaller; the app calls `Install.ps1 -Unattended` (exit 0 OK, 1 failed, 3 installed with problems) |
| `src/CouchCompanion.Setup.ps1` | Install helpers: copy files, compile helper, tasks, shutdown script (local GPO) |
| `src/CouchCompanion.Core.ps1` | TV protocol code in PowerShell (WS client, IP Control, WoL, discovery) |
| `src/CouchCompanion.ps1` | CLI: `On`, `Off`, `Input`, `Status`, `Pair`, `Discover` |
| `src/Helper/*.cs` | `CouchCompanionHelper` - ~2 MB background process: TV off on sleep, on at resume, wake on input, keep off. Compiled on the user's PC at install time |
| `docs/how-it-works.md` | Protocol and Windows findings |

Installed to `C:\ProgramData\CouchCompanion` (scripts, `config.json` with TV pairing tokens, logs);
helper logs in `%LOCALAPPDATA%\CouchCompanion`.

## Build

No SDK, no NuGet: everything is compiled by the **.NET Framework 4.x `csc.exe` that ships with
Windows** (C# 5 only - no `$"..."`, `?.`, `nameof`, expression-bodied members, etc.). PowerShell code
must run on **Windows PowerShell 5.1**.

Setup app, without starting it (same command as `Setup.cmd`), from the repo root in PowerShell:

```powershell
$net="$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
& "$net\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 "/win32manifest:src\Gui\app.manifest" "/reference:$net\WPF\PresentationFramework.dll" "/reference:$net\WPF\PresentationCore.dll" "/reference:$net\WPF\WindowsBase.dll" /reference:System.Xaml.dll "/resource:src\Gui\MainWindow.xaml,MainWindow.xaml" "/out:build\Couch Companion Setup.exe" "src\Gui\*.cs"
```

Helper compile check (the installer does this on install, see `CouchCompanion.Setup.ps1`):

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:build\CouchCompanionHelper.exe src\Helper\*.cs
```

`build/`, `*.exe`, `*.dll` and `config.json` are git-ignored: **never commit binaries or pairing tokens.**
There are no automated tests; a real test needs a Samsung TV on the network.

## Use

- Users: double-click `Setup.cmd` (admin), **Find** the TV, pick the PC's input, **Install**, accept
  the prompts on the TV. The same app updates and uninstalls.
- The exe finds the scripts via `--src <path>`, `..\src` or `src` next to itself, so a release ships
  the exe with `src\` beside it.
- After install, the CLI: `powershell -File C:\ProgramData\CouchCompanion\CouchCompanion.ps1 Status`.

## Release

1. Bump the version: new section in `CHANGELOG.md`.
2. Build the setup app (above) and package `build\CouchCompanion-<version>.zip` containing
   `CouchCompanion-<version>\` with `Couch Companion Setup.exe`, `src\`, both READMEs, `LICENSE`,
   `CHANGELOG.md`.
3. Tag `v<version>` and attach the zip to the release with the changelog section as notes.

## Conventions

- Don't run the installer, uninstaller, `Setup.cmd` or the CLI `On`/`Off` unasked: they change system
  settings (scheduled tasks, local group policy) or a real TV.
- TV quirks are model-specific: never "simplify" a retry, delay or fallback without reading why it's
  there in `docs/how-it-works.md`.
- Commit messages: short, imperative, no prefix convention.
