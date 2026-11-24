<#
.SYNOPSIS
    Turns a Samsung Tizen TV on or off over the network.

.DESCRIPTION
    On       Wake-on-LAN / KEY_POWER until the TV reports it is on (up to -Timeout seconds),
             then switch it to the PC's input (config pcInput) if set.
    Input    Switch the TV to the PC's input now (needs pcInput and IP Control pairing).
    Off      Sends KEY_POWER through the TV remote-control API, only if the TV is on.
    Status   Shows IP, power state and pairing.
    Pair     Connects once so the TV shows the "Allow" prompt and returns a token.
    Discover Lists Samsung TVs on the local network (no config needed).

    Settings are read from config.json next to this script (written by the installer).

.PARAMETER OnShutdown
    Used by the Windows shutdown script. With keepOnRestart=true in config.json the TV is
    left on when Windows is restarting instead of shutting down.

.EXAMPLE
    .\CouchCompanion.ps1 Off
.EXAMPLE
    .\CouchCompanion.ps1 Discover
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('On', 'Off', 'Status', 'Pair', 'Discover', 'Input')]
    [string]$Action = 'Status',
    [switch]$OnShutdown,
    [string]$ConfigPath,   # default: config.json next to this script
    [int]$Timeout = 60
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ConfigPath) { $ConfigPath = Join-Path $here 'config.json' }
. (Join-Path $here 'CouchCompanion.Core.ps1')
$script:TvLogTag = $Action
if ($Action -ne 'Discover') { $script:TvLogPath = Join-Path (Split-Path -Parent $ConfigPath) 'CouchCompanion.log' }

try {
    switch ($Action) {
        'Discover' {
            $tvs = @(Find-TvDevices)
            if (-not $tvs) { Write-Host 'No Samsung Tizen TV found (it must be on and on the same network).' }
            $tvs | Format-Table Name, Model, Ip, Mac, Network, PowerState -AutoSize
        }
        'Status' {
            $cfg = Get-TvConfig $ConfigPath
            $ip = Resolve-TvAddress $cfg
            $state = if ($ip) { Get-TvPowerState $ip } else { 'unreachable (off)' }
            Write-TvLog ('ip={0} mac={1} state={2} paired={3} keepOnRestart={4} wakeOnInput={5} keepOffAfterRemote={6} pcInput={7} ipControl={8}' -f $cfg.ip, $cfg.mac, $state, [bool]$cfg.token, $cfg.keepOnRestart, $cfg.wakeOnInput, $cfg.keepOffAfterRemote, $cfg.pcInput, [bool]$cfg.ipControlToken)
            if ($ip -and $state -eq 'on' -and $cfg.ipControlToken) {
                try { Write-TvLog "current input: $(Get-TvInput $cfg)" } catch { Write-TvLog "IP Control: $($_.Exception.Message)" }
            }
        }
        'Pair' {
            $cfg = Get-TvConfig $ConfigPath
            Write-TvLog 'connecting - accept the prompt on the TV...'
            Invoke-TvPair $cfg 90
            Write-TvLog 'paired'
        }
        'Off' {
            $cfg = Get-TvConfig $ConfigPath
            if ($OnShutdown -and $cfg.keepOnRestart -and (Test-RestartInProgress)) {
                Write-TvLog 'Windows is restarting - TV stays on'
                break
            }
            [void](Invoke-TvOff $cfg)
        }
        'On' {
            $cfg = Get-TvConfig $ConfigPath
            # The same task runs at boot, logon and resume. On a resume, the watcher (running while a
            # user is logged on) handles it itself, faster, and only when a person woke the PC. Without
            # it, still ignore wake-ups by timers (Windows Update, wake alarms) and the network.
            $resume = Get-TvResumeInfo
            if ($resume.IsResume) {
                if (Get-Process -Name CouchCompanionHelper -ErrorAction SilentlyContinue) { Write-TvLog 'resume: left to the watcher'; break }
                if ($resume.Unattended) { Write-TvLog "resume by $($resume.Source) - nobody there, TV left alone"; break }
            }
            # already on = someone was using the TV (other input or an app): switch to the PC even if
            # it reports the PC input
            $wasOn = (Get-TvPowerState $cfg.ip) -eq 'on'
            if (-not (Invoke-TvOn $cfg $Timeout)) { exit 2 }
            [void](Set-TvPcInput $cfg -Force:$wasOn)
        }
        'Input' {
            # Switch the TV to the PC's input now (config pcInput), e.g. for a hotkey.
            $cfg = Get-TvConfig $ConfigPath
            if (-not $cfg.pcInput) { Write-TvLog 'pcInput is not set in config.json'; exit 1 }
            if (-not (Set-TvPcInput $cfg)) { exit 2 }
        }
    }
} catch {
    Write-TvLog "ERROR: $($_.Exception.Message)"
    exit 1
}
