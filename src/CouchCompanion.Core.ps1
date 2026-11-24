<#
  CouchCompanion.Core.ps1 - functions shared by the CLI, the installer and the uninstaller.
  Dot-source it:  . "$PSScriptRoot\CouchCompanion.Core.ps1"

  Protocol notes (details in each section)
  - Power state, apps : GET http://<tv>:8001/api/v2/  ->  device.PowerState = on | standby
                        GET http://<tv>:8001/api/v2/applications/<id>  ->  visible = true | false
  - IP Control        : JSON-RPC over https://<tv>:1516/ ("IP Remote" on the TV): explicit power
                        on/off, read/set the input source. First choice when paired.
  - Remote keys       : WebSocket wss://<tv>:8002/api/v2/channels/samsung.remote.control
                        (first connection shows an "Allow" prompt on the TV and returns a token).
                        KEY_POWER toggles; on 2023 models it also wakes the TV from network standby.
  - Wake-on-LAN       : UDP 9/7/40000 (2022+ models only answer on 40000); for a TV that has left
                        the network.
#>

$script:TvLogPath = $null
$script:TvLogTag = ''

function Write-TvLog([string]$Message) {
    $line = '{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}' -f (Get-Date), $script:TvLogTag, $Message
    Write-Host $line
    if (-not $script:TvLogPath) { return }
    try {
        Add-Content -LiteralPath $script:TvLogPath -Value $line
        $all = @(Get-Content -LiteralPath $script:TvLogPath)
        if ($all.Count -gt 500) { $all | Select-Object -Last 300 | Set-Content -LiteralPath $script:TvLogPath }
    } catch {}
}

# ---------------------------------------------------------------------------- config

function Get-TvConfig([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Config not found: $Path (run the installer first)" }
    $c = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    # pcInput: the TV input this PC is connected to, e.g. "HDMI1" ("" = never switch the input)
    # ipControlToken: token for the TV's "IP Control" service (port 1516), used to read/set the input
    foreach ($k in 'name', 'ip', 'mac', 'token', 'pcInput', 'ipControlToken') {
        if (-not ($c.PSObject.Properties.Name -contains $k)) { $c | Add-Member -NotePropertyName $k -NotePropertyValue '' }
    }
    # tvApps: extra TV app ids that count as "not the PC" (besides the built-in list)
    if (-not ($c.PSObject.Properties.Name -contains 'tvApps')) { $c | Add-Member -NotePropertyName tvApps -NotePropertyValue @() }
    $defaults = @{ keepOnRestart = $true; wakeOnInput = $false; wakeOnInputGraceSeconds = 15; keepOffAfterRemote = $false }
    foreach ($k in $defaults.Keys) {
        if (-not ($c.PSObject.Properties.Name -contains $k)) { $c | Add-Member -NotePropertyName $k -NotePropertyValue $defaults[$k] }
    }
    $c | Add-Member -NotePropertyName _path -NotePropertyValue $Path -Force
    $c
}

function Save-TvConfig($Config) {
    $out = [ordered]@{
        name          = $Config.name
        ip            = $Config.ip
        mac           = $Config.mac
        token         = $Config.token
        keepOnRestart = [bool]$Config.keepOnRestart
        wakeOnInput   = [bool]$Config.wakeOnInput
        wakeOnInputGraceSeconds = [int]$Config.wakeOnInputGraceSeconds
        keepOffAfterRemote = [bool]$Config.keepOffAfterRemote
        pcInput       = [string]$Config.pcInput
        ipControlToken = [string]$Config.ipControlToken
        tvApps        = @($Config.tvApps | Where-Object { $_ } | ForEach-Object { [string]$_ })
    }
    $out | ConvertTo-Json | Set-Content -LiteralPath $Config._path -Encoding ASCII
}

# ---------------------------------------------------------------------------- discovery / state

function Get-TvInfo([string]$Ip, [int]$TimeoutSec = 2) {
    if (-not $Ip) { return $null }
    try { (Invoke-RestMethod "http://${Ip}:8001/api/v2/" -TimeoutSec $TimeoutSec -UseBasicParsing).device } catch { $null }
}

function Get-TvPowerState([string]$Ip) {
    # 'on', 'standby' or $null (unreachable). Models without PowerState report 'on' while reachable.
    $d = Get-TvInfo $Ip
    if (-not $d) { return $null }
    if ($d.PSObject.Properties.Name -contains 'PowerState' -and $d.PowerState) { return [string]$d.PowerState }
    'on'
}

function Get-MacFromArp([string]$Ip) {
    $n = Get-NetNeighbor -IPAddress $Ip -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($n -and $n.LinkLayerAddress -and $n.LinkLayerAddress -ne '00-00-00-00-00-00') {
        return ($n.LinkLayerAddress -replace '-', ':').ToUpper()
    }
    $null
}

function Get-LocalSubnets {
    # /24 around every active IPv4 address that has a default gateway
    Get-NetIPConfiguration -ErrorAction SilentlyContinue |
        Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } |
        ForEach-Object { $_.IPv4Address } |
        ForEach-Object { (($_.IPAddress -split '\.')[0..2]) -join '.' } |
        Select-Object -Unique
}

function Find-TvDevices([string[]]$Subnets = (Get-LocalSubnets)) {
    $probes = foreach ($net in $Subnets) {
        foreach ($i in 1..254) {
            $c = New-Object Net.Sockets.TcpClient
            [pscustomobject]@{ Ip = "$net.$i"; Client = $c; Task = $c.ConnectAsync("$net.$i", 8001) }
        }
    }
    try { [void][Threading.Tasks.Task]::WaitAll(@($probes.Task), 2500) } catch {}
    $open = @($probes | Where-Object { $_.Task.Status -eq 'RanToCompletion' -and $_.Client.Connected } | ForEach-Object { $_.Ip })
    $probes | ForEach-Object { $_.Client.Close() }

    foreach ($ip in $open) {
        $d = Get-TvInfo $ip
        if (-not $d -or [string]$d.OS -ne 'Tizen') { continue }
        $mac = Get-MacFromArp $ip
        if (-not $mac -and $d.PSObject.Properties.Name -contains 'wifiMac') { $mac = ([string]$d.wifiMac).ToUpper() }
        [pscustomobject]@{
            Name       = [string]$d.name
            Model      = [string]$d.modelName
            Ip         = $ip
            Mac        = $mac
            Network    = [string]$d.networkType
            PowerState = $(if ($d.PSObject.Properties.Name -contains 'PowerState') { [string]$d.PowerState } else { 'on' })
            TokenAuth  = [string]$d.TokenAuthSupport
        }
    }
}

function Resolve-TvAddress($Config) {
    # Returns the TV IP if it answers, following it by MAC if the router gave it a new address.
    if (Get-TvInfo $Config.ip) { return $Config.ip }
    foreach ($tv in @(Find-TvDevices)) {
        if ($tv.Mac -and $tv.Mac -eq ([string]$Config.mac).ToUpper()) {
            if ($tv.Ip -ne $Config.ip) {
                Write-TvLog "TV moved from $($Config.ip) to $($tv.Ip)"
                $Config.ip = $tv.Ip
                try { Save-TvConfig $Config } catch { Write-TvLog "could not save new IP: $($_.Exception.Message)" }
            }
            return $tv.Ip
        }
    }
    $null
}

# ---------------------------------------------------------------------------- Wake-on-LAN

function Send-WakeOnLan([string]$Mac, [string]$Ip) {
    $bytes = [byte[]]($Mac -split '[:-]' | ForEach-Object { [Convert]::ToByte($_, 16) })
    if ($bytes.Count -ne 6) { throw "Invalid MAC address: $Mac" }
    $packet = [byte[]](@(0xFF) * 6 + ($bytes * 16))
    $targets = @('255.255.255.255')
    if ($Ip) { $targets += ((($Ip -split '\.')[0..2]) -join '.') + '.255'; $targets += $Ip }
    $udp = New-Object Net.Sockets.UdpClient
    $udp.EnableBroadcast = $true
    try {
        foreach ($t in $targets) {
            foreach ($port in 9, 7, 40000) { try { [void]$udp.Send($packet, $packet.Length, $t, $port) } catch {} }
        }
    } finally { $udp.Close() }
}

# ---------------------------------------------------------------------------- WebSocket (hand-rolled)
# .NET Framework's ClientWebSocket loses the first frame when the TV sends it in the same
# TCP segment as the HTTP 101 response, so the connection looked "silent" forever.
# A minimal RFC 6455 client over SslStream avoids that.

if (-not ('SamsungTvTls' -as [type])) {
    Add-Type @'
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class SamsungTvTls {
    // The TV uses a self-signed certificate on port 8002.
    static bool Accept(object s, X509Certificate c, X509Chain ch, SslPolicyErrors e) { return true; }
    public static readonly RemoteCertificateValidationCallback Callback = Accept;
}
'@
}

function Read-Exact($Stream, [int]$Count) {
    $buf = New-Object byte[] $Count
    $read = 0
    while ($read -lt $Count) {
        $n = $Stream.Read($buf, $read, $Count - $read)
        if ($n -le 0) { throw 'connection closed by the TV' }
        $read += $n
    }
    , $buf
}

function Read-WsFrame($Stream) {
    $h = Read-Exact $Stream 2
    $op = $h[0] -band 0x0F
    $len = [int64]($h[1] -band 0x7F)
    if ($len -eq 126) { $e = Read-Exact $Stream 2; $len = ([int64]$e[0] -shl 8) -bor $e[1] }
    elseif ($len -eq 127) { $e = Read-Exact $Stream 8; $len = [int64]0; foreach ($x in $e) { $len = ($len -shl 8) -bor $x } }
    [byte[]]$data = if ($len -gt 0) { Read-Exact $Stream ([int]$len) } else { @() }
    [pscustomobject]@{ Opcode = $op; Text = [Text.Encoding]::UTF8.GetString($data) }
}

function Send-WsFrame($Stream, [byte[]]$Data, [int]$Opcode = 1) {
    $f = New-Object Collections.Generic.List[byte]
    $f.Add([byte](0x80 -bor $Opcode))
    if ($Data.Length -lt 126) { $f.Add([byte](0x80 -bor $Data.Length)) }
    else { $f.Add([byte](0x80 -bor 126)); $f.Add([byte]($Data.Length -shr 8)); $f.Add([byte]($Data.Length -band 0xFF)) }
    $mask = [byte[]](1..4 | ForEach-Object { Get-Random -Maximum 256 })
    $f.AddRange($mask)
    for ($i = 0; $i -lt $Data.Length; $i++) { $f.Add([byte]($Data[$i] -bxor $mask[$i % 4])) }
    $a = $f.ToArray()
    $Stream.Write($a, 0, $a.Length)
    $Stream.Flush()
}

function Connect-TvRemote($Config, [string]$Ip, [int]$TimeoutSec = 8) {
    $tcp = New-Object Net.Sockets.TcpClient
    if (-not $tcp.ConnectAsync($Ip, 8002).Wait(4000)) { $tcp.Close(); throw 'TV did not answer on port 8002' }
    try {
        $tcp.ReceiveTimeout = 5000
        $ssl = New-Object Net.Security.SslStream($tcp.GetStream(), $false, [SamsungTvTls]::Callback)
        $ssl.AuthenticateAsClient($Ip, $null, [Security.Authentication.SslProtocols]::Tls12, $false)

        $name = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Config.name))
        $path = "/api/v2/channels/samsung.remote.control?name=$name"
        if ($Config.token) { $path += "&token=$($Config.token)" }
        $key = [Convert]::ToBase64String([byte[]](1..16 | ForEach-Object { Get-Random -Maximum 256 }))
        $req = "GET $path HTTP/1.1`r`nHost: ${Ip}:8002`r`nUpgrade: websocket`r`nConnection: Upgrade`r`n" +
               "Sec-WebSocket-Key: $key`r`nSec-WebSocket-Version: 13`r`n`r`n"
        $b = [Text.Encoding]::ASCII.GetBytes($req)
        $ssl.Write($b, 0, $b.Length); $ssl.Flush()

        # HTTP headers byte by byte, so the first WebSocket frame is not swallowed
        $hdr = New-Object Text.StringBuilder
        while (-not $hdr.ToString().EndsWith("`r`n`r`n")) { [void]$hdr.Append([char](Read-Exact $ssl 1)[0]) }
        if ($hdr.ToString() -notmatch '^HTTP/1\.1 101') { throw "handshake refused: $(($hdr.ToString() -split "`r`n")[0])" }

        $tcp.ReceiveTimeout = $TimeoutSec * 1000
        while ($true) {
            try { $fr = Read-WsFrame $ssl } catch { throw 'no answer from the TV (was the "Allow" prompt accepted?)' }
            if ($fr.Opcode -eq 8) { throw 'TV closed the connection' }
            if ($fr.Opcode -ne 1) { continue }
            $msg = $fr.Text | ConvertFrom-Json
            if ($msg.event -eq 'ms.channel.connect') { break }
            if ($msg.event -eq 'ms.channel.unauthorized') { throw 'TV refused the connection (PC denied in the TV device list?)' }
        }
        if ($msg.PSObject.Properties.Name -contains 'data' -and $msg.data -and
            $msg.data.PSObject.Properties.Name -contains 'token' -and $msg.data.token -and
            [string]$msg.data.token -ne $Config.token) {
            $Config.token = [string]$msg.data.token
            Save-TvConfig $Config
            Write-TvLog 'pairing token saved'
        }
        [pscustomobject]@{ Tcp = $tcp; Stream = $ssl }
    } catch {
        $tcp.Close()
        throw
    }
}

function Disconnect-TvRemote($Connection) {
    try { Send-WsFrame $Connection.Stream ([byte[]](0x03, 0xE8)) 8 } catch {}
    $Connection.Tcp.Close()
}

function Send-TvKey($Config, [string]$Ip, [string]$Key) {
    $cx = Connect-TvRemote $Config $Ip 8
    try {
        $cmd = @{ method = 'ms.remote.control'; params = @{ Cmd = 'Click'; DataOfCmd = $Key; Option = 'false'; TypeOfRemote = 'SendRemoteKey' } } |
               ConvertTo-Json -Compress
        # A key sent right after "connect" is sometimes ignored; give the TV a moment,
        # and keep the connection open a little after sending.
        Start-Sleep -Milliseconds 1000
        Send-WsFrame $cx.Stream ([Text.Encoding]::UTF8.GetBytes($cmd))
        Start-Sleep -Milliseconds 1500
    } finally { Disconnect-TvRemote $cx }
}

# ---------------------------------------------------------------------------- IP Control (input source)
# Samsung "IP Control": JSON-RPC 2.0 over HTTPS on port 1516 (needs "IP Remote" enabled on the TV).
# Unlike the remote-control WebSocket it can READ and SET the input source. Found the hard way on a
# QE55Q80C (2023), where KEY_HDMI1..4 are ignored:
#  - send headers and body in ONE write, and do NOT send "Connection: close": otherwise the TV
#    answers {"code": -32700, "message": "Parse error"} to a perfectly valid request;
#  - the same "Parse error" is the answer to a missing or stale AccessToken;
#  - the TV keeps ONE token: pairing again (createAccessToken) invalidates the previous one;
#  - getTVStates / inputSourceControl return e.g. {"result":{"inputSource":"HDMI1"}}.

function Invoke-TvIpControl([string]$Ip, [string]$Method, [hashtable]$Params, [string]$Token, [int]$TimeoutSec = 8) {
    $body = [ordered]@{ jsonrpc = '2.0'; id = '1'; method = $Method }
    if ($Token -or $Params) {
        $p = [ordered]@{}
        if ($Token) { $p.AccessToken = $Token }
        if ($Params) { foreach ($k in $Params.Keys) { $p[$k] = $Params[$k] } }
        $body.params = $p
    }
    $bb = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Compress))
    $head = "POST / HTTP/1.1`r`nHost: ${Ip}:1516`r`nAccept: application/json`r`nContent-Type: application/json`r`nContent-Length: $($bb.Length)`r`n`r`n"
    $tcp = New-Object Net.Sockets.TcpClient
    try {
        if (-not $tcp.ConnectAsync($Ip, 1516).Wait(3000)) { throw 'IP Control port 1516 not reachable' }
        $tcp.ReceiveTimeout = $TimeoutSec * 1000
        $ssl = New-Object Net.Security.SslStream($tcp.GetStream(), $false, [SamsungTvTls]::Callback)
        $ssl.AuthenticateAsClient($Ip, $null, [Security.Authentication.SslProtocols]::Tls12, $false)
        $all = [Text.Encoding]::ASCII.GetBytes($head) + $bb
        $ssl.Write($all, 0, $all.Length); $ssl.Flush()
        $ms = New-Object IO.MemoryStream; $buf = New-Object byte[] 8192
        while ($true) {
            $n = $ssl.Read($buf, 0, $buf.Length); if ($n -le 0) { break }
            $ms.Write($buf, 0, $n)
            $t = [Text.Encoding]::UTF8.GetString($ms.ToArray())
            $i = $t.IndexOf("`r`n`r`n")
            if ($i -ge 0) {
                $m = [regex]::Match($t.Substring(0, $i), '(?i)content-length:\s*(\d+)')
                if ($m.Success -and [Text.Encoding]::UTF8.GetByteCount($t.Substring($i + 4)) -ge [int]$m.Groups[1].Value) { break }
            }
        }
        $t = [Text.Encoding]::UTF8.GetString($ms.ToArray())
        $r = $t.Substring($t.IndexOf("`r`n`r`n") + 4) | ConvertFrom-Json
        if ($r.PSObject.Properties.Name -contains 'code') { throw "IP Control error $($r.code): $($r.message)$(if ($r.code -eq -32700) { ' (token rejected - pair again)' })" }
        if ($r.PSObject.Properties.Name -contains 'error') { throw "IP Control error $($r.error.code): $($r.error.message)" }
        $r.result
    } finally { $tcp.Close() }
}

function Test-TvIpControlPort([string]$Ip) {
    $t = New-Object Net.Sockets.TcpClient
    try { $t.ConnectAsync($Ip, 1516).Wait(2500) -and $t.Connected } catch { $false } finally { $t.Close() }
}

function New-TvIpControlToken($Config) {
    # Shows an "allow" prompt on the TV; returns the token once it is accepted.
    $r = Invoke-TvIpControl $Config.ip 'createAccessToken' $null $null 45
    if (-not $r.AccessToken) { throw 'IP Control pairing returned no token' }
    [string]$r.AccessToken
}

function Get-TvInput($Config) {
    [string](Invoke-TvIpControl $Config.ip 'inputSourceControl' $null $Config.ipControlToken).inputSource
}

function Set-TvPcInput($Config, [int]$TimeoutSec = 25, [switch]$Force) {
    # Switch the TV to the PC's input (config pcInput), checking first and verifying after.
    # Right after power-on the IP Control service needs a few seconds, so keep trying.
    # A visible app (the TV still reports the last HDMI input then) also means "switch";
    # -Force switches once anyway (setting the current input only flashes the source banner).
    if (-not $Config.pcInput -or -not $Config.ipControlToken) { return $true }
    $target = ([string]$Config.pcInput).ToUpper()
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $switched = $false; $lastErr = ''
    while ((Get-Date) -lt $deadline) {
        try {
            $cur = (Get-TvInput $Config).ToUpper()
            $app = Get-TvVisibleApp $Config
            if ($cur -eq $target -and -not $app -and -not ($Force -and -not $switched)) {
                if ($switched) { Write-TvLog "input switched to $target" }
                return $true
            }
            Write-TvLog "TV shows $(if ($app) { "the app $app" } else { $cur }) - switching to $target"
            [void](Invoke-TvIpControl $Config.ip 'inputSourceControl' @{ inputSource = $target } $Config.ipControlToken)
            $switched = $true
            Start-Sleep -Milliseconds 1500
        } catch {
            $lastErr = $_.Exception.Message
            if ($lastErr -match 'token rejected') { Write-TvLog "cannot switch input: $lastErr"; return $false }
            Start-Sleep -Seconds 1
        }
    }
    Write-TvLog "could not switch input to $target within $TimeoutSec s ($lastErr)"
    $false
}

function Test-TvIpControl($Config) { [bool]($Config.ipControlToken) }

function Set-TvPowerIp($Config, [bool]$On) {
    # IP Control power: explicit on/off (unlike KEY_POWER, which toggles), ~0.1 s, and the service
    # keeps answering in network standby. Returns $false if IP Control is not set up or did not answer.
    if (-not (Test-TvIpControl $Config)) { return $false }
    try {
        $r = Invoke-TvIpControl $Config.ip 'powerControl' @{ power = $(if ($On) { 'powerOn' } else { 'powerOff' }) } $Config.ipControlToken 5
        return [bool]$r.power
    } catch { Write-TvLog "IP Control power $(if ($On) { 'on' } else { 'off' }) failed: $($_.Exception.Message)"; return $false }
}

# ---------------------------------------------------------------------------- apps on the TV

# While an app (YouTube...) is on screen, IP Control still reports the last HDMI input. The TV cannot
# list the foreground app, but GET :8001/api/v2/applications/<id> says whether one app is visible, so
# known apps (and config "tvApps") are checked. Keep in sync with KnownAppIds in Helper.cs.
$script:TvKnownAppIds = @(
    '111299001912',   # YouTube
    '3201907018807',  # Netflix
    '11101200001',    # Netflix (older)
    '3201910019365',  # Prime Video
    '3201512006785',  # Prime Video (older)
    '3201901017640',  # Disney+
    '3201606009684',  # Spotify
    '3201807016597',  # Apple TV
    '3202301029760',  # HBO Max
    '3201601007230',  # HBO Max (older)
    '3201512006963',  # Plex
    '3202010022079',  # Browser
    'org.tizen.browser'
)

function Get-TvVisibleApp($Config) {
    # Name of a known app on screen, '' if none, $null if the TV did not answer at all.
    $ids = @($script:TvKnownAppIds) + @($Config.tvApps | Where-Object { $_ })
    $any = $false
    foreach ($id in ($ids | Select-Object -Unique)) {
        try { $a = Invoke-RestMethod "http://$($Config.ip):8001/api/v2/applications/$id" -TimeoutSec 2 -UseBasicParsing } catch { continue }
        $any = $true
        if ($a.visible) { return [string]$a.name }
    }
    if ($any) { '' } else { $null }
}

function Test-TvShowsPc($Config) {
    # $true / $false when known, $null when it cannot be told (no pcInput, no IP Control, no answer).
    # When an app is the reason for $false, its name is left in $script:TvShownApp (for the log).
    $script:TvShownApp = $null
    if (-not $Config.pcInput -or -not (Test-TvIpControl $Config)) { return $null }
    try { $onPcInput = (Get-TvInput $Config).ToUpper() -eq ([string]$Config.pcInput).ToUpper() } catch { return $null }
    if (-not $onPcInput) { return $false }
    $app = Get-TvVisibleApp $Config
    if ($app) { $script:TvShownApp = $app; return $false }
    $true
}

# ---------------------------------------------------------------------------- resume classification

function Get-TvResumeInfo([int]$WithinSeconds = 180) {
    # Was this run triggered by a resume, and was the PC woken by a person or by a timer?
    # Power-Troubleshooter event 1: WakeSourceType 1 = power button, 5 = device (keyboard, USB,
    # but also "ACPI Wake Alarm" and the network card), 6 = timer (e.g. Windows Update), 0 = unknown.
    $ev = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Power-Troubleshooter'; Id = 1; StartTime = (Get-Date).AddSeconds(-$WithinSeconds) } -MaxEvents 1 -ErrorAction SilentlyContinue
    if (-not $ev) { return [pscustomobject]@{ IsResume = $false; Unattended = $false; Source = '' } }
    $d = @{}; ([xml]$ev.ToXml()).Event.EventData.Data | ForEach-Object { $d[$_.Name] = $_.'#text' }
    $type = [int]$d['WakeSourceType']; $text = [string]$d['WakeSourceText']
    $unattended = $type -eq 6 -or $text -match 'Wake Alarm|GbE|Ethernet|Network|Wi-?Fi|Wireless|LAN'
    [pscustomobject]@{ IsResume = $true; Unattended = $unattended; Source = "type $type $text".Trim() }
}

# ---------------------------------------------------------------------------- high level

function Invoke-TvPair($Config, [int]$TimeoutSec = 90) {
    $ip = Resolve-TvAddress $Config
    if (-not $ip) { throw 'TV not found on the network - it must be ON to pair' }
    Disconnect-TvRemote (Connect-TvRemote $Config $ip $TimeoutSec)
}

function Invoke-TvOff($Config, [switch]$Force) {
    # Turns the TV off - but only while it shows this PC (config pcInput): someone watching another
    # device (e.g. a set-top box) keeps it. -Force skips that check (installer power test).
    $ip = Resolve-TvAddress $Config
    if ((Get-TvPowerState $ip) -ne 'on') { Write-TvLog 'TV already off'; return $true }
    if (-not $Force) {
        $showsPc = Test-TvShowsPc $Config
        if ($showsPc -eq $false) {
            $what = if ($script:TvShownApp) { "the app $script:TvShownApp" } else { try { Get-TvInput $Config } catch { 'another input' } }
            Write-TvLog "TV shows $what, not the PC ($($Config.pcInput)) - left on"; return $true
        }
    }
    if (Set-TvPowerIp $Config $false) {
        for ($s = 0; $s -lt 6; $s++) {
            Start-Sleep -Seconds 1
            if ((Get-TvPowerState $ip) -ne 'on') { Write-TvLog 'TV off (IP Control)'; return $true }
        }
        Write-TvLog 'TV still on 6 s after IP Control power off - trying KEY_POWER'
    }
    # Fallback: KEY_POWER. Over Wi-Fi a key press is occasionally lost, so confirm and retry once. Wait
    # long enough first: KEY_POWER toggles, and a second press on a TV that is already going off turns it on.
    for ($try = 1; $try -le 2; $try++) {
        Send-TvKey $Config $ip 'KEY_POWER'
        for ($s = 0; $s -lt 6; $s++) {
            Start-Sleep -Seconds 1
            if ((Get-TvPowerState $ip) -ne 'on') { Write-TvLog 'TV off'; return $true }
        }
        if ($try -eq 1) { Write-TvLog 'TV still on after 6 s, sending power-off again' }
    }
    Write-TvLog 'TV did not turn off'
    $false
}

function Set-WakeMarker($Config) {
    # Tells the watcher "this wake-up was on purpose", so it does not take it for the TV turning
    # itself back on (keep-off mode). The watcher reads the file's timestamp.
    try { Set-Content -LiteralPath (Join-Path (Split-Path -Parent $Config._path) 'last-wake.txt') -Value (Get-Date -Format o) } catch {}
}

function Invoke-TvOn($Config, [int]$TimeoutSec = 60) {
    # Mark first, even if the TV is already on: after a resume the watcher must not take the TV
    # "coming back" (it only looked offline because the PC's network was down) for a self-wake.
    Set-WakeMarker $Config
    if ((Get-TvPowerState $Config.ip) -eq 'on') { Write-TvLog 'TV already on'; return $true }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $i = 0
    $lastKey = [datetime]::MinValue
    $started = Get-Date
    $state = Get-TvPowerState $Config.ip
    $ipWorks = $false; $lastIp = [datetime]::MinValue
    while ((Get-Date) -lt $deadline) {
        # First choice: IP Control "powerOn" - explicit (cannot turn the TV off by mistake), works from
        # network standby in ~1.5 s. Repeating it is harmless.
        if ((Test-TvIpControl $Config) -and ((Get-Date) - $lastIp).TotalSeconds -ge 3) {
            $lastIp = Get-Date
            $ipWorks = Set-TvPowerIp $Config $true
            if ($ipWorks) { Write-TvLog 'sent IP Control power on' }
        }
        # Newer models (tested: QE55Q80C, 2023) wake from network standby with KEY_POWER over the
        # remote API, while Wake-on-LAN is often ignored in that state. Only send it when the TV
        # reports 'standby' (KEY_POWER toggles), and not more often than every 10 s.
        # When the watcher runs (user logged on) it wakes the TV itself, faster; leave KEY_POWER to
        # it for 15 s so two senders cannot cancel each other out, and only step in if it did not.
        $watcherUp = [bool](Get-Process -Name CouchCompanionHelper -ErrorAction SilentlyContinue)
        # KEY_POWER only when IP Control did not answer: a toggle sent while the TV is already starting
        # up from "powerOn" would switch it straight back off.
        $mayPress = (-not $watcherUp -or ((Get-Date) - $started).TotalSeconds -ge 15) -and -not $ipWorks
        if ($state -eq 'standby' -and $mayPress -and ((Get-Date) - $lastKey).TotalSeconds -ge 10) {
            $lastKey = Get-Date
            try { Send-TvKey $Config $Config.ip 'KEY_POWER'; Write-TvLog 'TV in network standby - sent KEY_POWER' }
            catch { Write-TvLog "KEY_POWER in standby failed: $($_.Exception.Message)" }
        }
        Send-WakeOnLan $Config.mac $Config.ip
        Start-Sleep -Seconds 2
        $state = Get-TvPowerState $Config.ip
        if (-not $state -and (++$i % 5) -eq 0) { [void](Resolve-TvAddress $Config); $state = Get-TvPowerState $Config.ip }
        if ($state -eq 'on') { Write-TvLog 'TV on'; return $true }
    }
    Write-TvLog "TV did not turn on within $TimeoutSec s"
    $false
}

function Test-RestartInProgress {
    # Windows logs event 1074 (User32) with the shutdown type before running shutdown scripts.
    # The type text is localized, so match the common languages; unknown means "shutdown".
    $ev = Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 1074; StartTime = (Get-Date).AddMinutes(-10) } -MaxEvents 1 -ErrorAction SilentlyContinue
    if (-not $ev) { return $false }
    $type = [string]$ev.Properties[4].Value
    $restart = 'restart|reinic|neustart|red\u00e9marr|redemarr|riavvi|herstart|ponownie|\u043f\u0435\u0440\u0435\u0437\u0430\u0433\u0440\u0443\u0437|\u518d\u8d77\u52d5|\u91cd\u65b0\u542f\u52a8|\u91cd\u65b0\u555f\u52d5|\ub2e4\uc2dc \uc2dc\uc791'
    $type -match $restart
}
