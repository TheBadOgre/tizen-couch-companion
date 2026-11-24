<#
.SYNOPSIS
    Couch Companion installer: finds the TV, pairs, tests power on/off and installs the hooks.

.PARAMETER TvAddress
    IP of the TV (skips network discovery).
.PARAMETER OnRestart
    Keep (default) leaves the TV on when Windows restarts; TurnOff turns it off on restarts too.
.PARAMETER WakeOnInput
    Yes installs a background component (CouchCompanionHelper) that turns the TV back on when the mouse or keyboard is used
    while the TV is off (e.g. after switching it off with the remote).
.PARAMETER KeepOff
    Yes lets the same component switch the TV off again over the network when it turns itself back on
    shortly after being switched off with the remote (HDMI hot-plug wake-up).
.PARAMETER SkipPowerTest
    Do not turn the TV off/on during installation.
#>
[CmdletBinding()]
param(
    [string]$TvAddress,
    [ValidateSet('Keep', 'TurnOff', '')][string]$OnRestart = '',
    [ValidateSet('Yes', 'No', '')][string]$WakeOnInput = '',
    [ValidateSet('Yes', 'No', '')][string]$KeepOff = '',
    [string]$PcInput,     # TV input the PC is on, e.g. HDMI1 ("-" = never switch)
    [int]$WakeGraceSeconds = 0,   # wake on input: TV must be off this long first (0 = keep / 15)
    [switch]$SkipPowerTest,
    # No prompts at all (used by the setup app): every choice comes from the parameters, questions
    # take their default answer, and a failure ends the script with exit code 1.
    # Exit codes: 0 = installed, 1 = failed, 3 = installed with problems (see the final check).
    [switch]$Unattended
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CouchCompanion.Core.ps1')
. (Join-Path $PSScriptRoot 'CouchCompanion.Setup.ps1')

$pt = (Get-UICulture).Name -like 'pt*'
function T([string]$En, [string]$Pt) { if ($script:pt) { $Pt } else { $En } }
function Step([string]$Text) { Write-Host "`n>> $Text" -ForegroundColor Cyan }
function Ok([string]$Text) { Write-Host "   OK  $Text" -ForegroundColor Green }
function Warn([string]$Text) { Write-Host "   !!  $Text" -ForegroundColor Yellow }
function Ask([string]$Text, [bool]$Default = $true) {
    if ($Unattended) { Write-Host "   $Text -> $(if ($Default) { 'yes' } else { 'no' })"; return $Default }
    $hint = if ($Default) { T '[Y/n]' '[S/n]' } else { T '[y/N]' '[s/N]' }
    $a = Read-Host "   $Text $hint"
    if (-not $a) { return $Default }
    $a -match '^(y|s)'
}
function Fail([string]$Text) { Write-Host "`n   $Text" -ForegroundColor Red; exit 1 }

Write-Host ''
Write-Host '  Couch Companion' -ForegroundColor White
Write-Host (T '  Your Samsung TV turns on and off together with your PC.' '  Sua TV Samsung liga e desliga junto com o PC.')

if (-not (Test-IsAdmin)) { Fail (T 'Run as administrator (use Setup.cmd).' 'Execute como administrador (use o Setup.cmd).') }
# Keep a copy of everything shown here, so an error is not lost when the window is closed.
New-Item -ItemType Directory -Path $script:InstallDir -Force | Out-Null
try { Start-Transcript -LiteralPath (Join-Path $script:InstallDir 'install.log') -Force | Out-Null } catch {}
if ((Get-CimInstance Win32_ComputerSystem).PartOfDomain) {
    Warn (T 'This PC is in a domain: domain policies may override the local shutdown script.' 'Este PC esta em um dominio: politicas do dominio podem sobrepor o script de desligamento local.')
}

# --------------------------------------------------------------------------- 1. TV settings
Step (T 'Before you start, on the TV:' 'Antes de comecar, na TV:')
Write-Host (T @'
   - Settings > General > Network > Expert Settings > Power On with Mobile = On
   - Settings > General > Network > Expert Settings > IP Remote = On (if present)
   - Settings > General > External Device Manager > Device Connection Manager
       > Access Notification = First Time Only
   - The TV must be ON and on the same network as this PC (cable is more reliable than Wi-Fi).
'@ @'
   - Configuracoes > Geral > Rede > Configuracoes especialistas > Ligar com dispositivo movel = Ativado
   - Configuracoes > Geral > Rede > Configuracoes especialistas > IP Remote = Ativado (se existir)
   - Configuracoes > Geral > Gerenciador de dispositivos externos > Gerenciador de conexao de dispositivos
       > Notificacao de acesso = Somente na primeira vez
   - A TV precisa estar LIGADA e na mesma rede do PC (cabo e mais confiavel que Wi-Fi).
'@)
if (-not $Unattended) { [void](Read-Host (T '   Press Enter when ready' '   Aperte Enter quando estiver pronto')) }

# --------------------------------------------------------------------------- 2. find the TV
Step (T 'Looking for Samsung TVs on the network...' 'Procurando TVs Samsung na rede...')
if ($TvAddress) {
    $tvs = @(Find-TvDevices -Subnets ((($TvAddress -split '\.')[0..2]) -join '.') | Where-Object { $_.Ip -eq $TvAddress })
} else {
    $tvs = @(Find-TvDevices)
}
if (-not $tvs) { Fail (T 'No TV found. Check that it is on, on the same network, and try -TvAddress <ip>.' 'Nenhuma TV encontrada. Confira se ela esta ligada e na mesma rede, ou use -TvAddress <ip>.') }
for ($i = 0; $i -lt $tvs.Count; $i++) {
    Write-Host ('   [{0}] {1}  {2}  {3}  {4}  ({5})' -f ($i + 1), $tvs[$i].Name, $tvs[$i].Model, $tvs[$i].Ip, $tvs[$i].Mac, $tvs[$i].Network)
}
$tv = $tvs[0]
if ($tvs.Count -gt 1) {
    if ($Unattended) { Fail (T 'More than one TV found: choose one with -TvAddress <ip>.' 'Mais de uma TV encontrada: escolha com -TvAddress <ip>.') }
    $n = Read-Host (T '   Which one? (number)' '   Qual delas? (numero)')
    $tv = $tvs[[int]$n - 1]
}
if (-not $tv.Mac) { Fail (T 'Could not read the TV MAC address (needed for Wake-on-LAN).' 'Nao consegui ler o endereco MAC da TV (necessario para o Wake-on-LAN).') }
if ($tv.Network -eq 'wireless') { Warn (T 'TV is on Wi-Fi: it works, but commands are occasionally lost. A network cable is strongly recommended.' 'A TV esta no Wi-Fi: funciona, mas as vezes um comando se perde. Cabo de rede e bem mais confiavel.') }
Ok "$($tv.Name) - $($tv.Ip)"

# --------------------------------------------------------------------------- 3. files + config
New-Item -ItemType Directory -Path $script:InstallDir -Force | Out-Null
if (Remove-LegacyInstall) { Ok (T 'previous version (SamsungTV-Companion) removed, settings kept' 'versao anterior (SamsungTV-Companion) removida, configuracoes mantidas') }
Stop-Watcher   # a running helper would react to the power test below
foreach ($f in 'CouchCompanion.ps1', 'CouchCompanion.Core.ps1', 'CouchCompanion.Setup.ps1', 'Uninstall.ps1') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $f) -Destination $script:InstallDir -Force
}
$cfgPath = Join-Path $script:InstallDir 'config.json'
$old = if (Test-Path -LiteralPath $cfgPath) { Get-TvConfig $cfgPath } else { $null }
$cfg = [pscustomobject]@{
    name          = "Couch Companion ($env:COMPUTERNAME)"
    ip            = $tv.Ip
    mac           = $tv.Mac
    token         = $(if ($old) { $old.token } else { '' })   # the token belongs to the PC name, not to the TV address
    keepOnRestart = $true
    wakeOnInput   = $false
    wakeOnInputGraceSeconds = $(if ($WakeGraceSeconds -gt 0) { $WakeGraceSeconds } elseif ($old) { $old.wakeOnInputGraceSeconds } else { 15 })
    keepOffAfterRemote = $false
    pcInput       = $(if ($old) { [string]$old.pcInput } else { '' })
    ipControlToken = $(if ($old) { [string]$old.ipControlToken } else { '' })   # the TV keeps one; re-pairing invalidates it
    _path         = $cfgPath
}
Save-TvConfig $cfg
Protect-InstallDir
$script:TvLogPath = Join-Path $script:InstallDir 'CouchCompanion.log'
$script:TvLogTag = 'Install'

# --------------------------------------------------------------------------- 4. pair
Step (T 'Pairing: accept the prompt on the TV ("Allow")...' 'Pareando: aceite o aviso na TV ("Permitir")...')
try { Invoke-TvPair $cfg 90; Ok (T 'paired' 'pareado') }
catch {
    $hint = T 'If the PC is listed as denied in the TV Device Connection Manager, allow or delete it and run again.' 'Se o PC aparecer como negado no Gerenciador de conexao de dispositivos da TV, permita ou apague e rode de novo.'
    Fail ((T 'Pairing failed: ' 'O pareamento falhou: ') + $_.Exception.Message + "`n   " + $hint)
}

# --------------------------------------------------------------------------- 5. power test
if (-not $SkipPowerTest -and (Ask (T 'Test now? The TV will turn OFF and back ON by itself (~1 min, do not use the remote).' 'Testar agora? A TV vai DESLIGAR e LIGAR sozinha (~1 min, nao use o controle).'))) {
    [void](Invoke-TvOff $cfg -Force)
    $t0 = Get-Date
    while (((Get-Date) - $t0).TotalSeconds -lt 30 -and (Get-TvPowerState $cfg.ip) -eq 'on') { Start-Sleep 2 }
    Start-Sleep -Seconds 15      # let the TV settle into network standby
    $t1 = Get-Date
    if (Invoke-TvOn $cfg 90) { Ok ((T 'TV turned on by itself in {0:N0} s' 'A TV ligou sozinha em {0:N0} s') -f ((Get-Date) - $t1).TotalSeconds) }
    else {
        Warn (T 'The TV did not turn on. Check "Power On with Mobile" on the TV; on Wi-Fi it may also need the router to forward broadcasts.' 'A TV nao ligou. Confira "Ligar com dispositivo movel" na TV; no Wi-Fi o roteador tambem precisa repassar broadcast.')
        if (-not (Ask (T 'Install anyway?' 'Instalar mesmo assim?') $false)) { Fail (T 'Cancelled. Turn the TV on with the remote.' 'Cancelado. Ligue a TV pelo controle.') }
    }
}

# --------------------------------------------------------------------------- 6. restart behaviour
if (-not $OnRestart) {
    $keep = Ask (T 'Keep the TV ON when Windows RESTARTS (only turn off on shutdown)?' 'Manter a TV LIGADA quando o Windows REINICIAR (so desligar no desligamento)?')
} else { $keep = $OnRestart -eq 'Keep' }
$cfg.keepOnRestart = $keep

if (-not $WakeOnInput) {
    $wake = Ask (T 'Turn the TV on when the mouse or keyboard is used while the TV is off? (background component CouchCompanionHelper, ~2 MB RAM)' 'Ligar a TV quando o mouse ou o teclado for usado com ela desligada? (componente em segundo plano CouchCompanionHelper, ~2 MB de RAM)')
} else { $wake = $WakeOnInput -eq 'Yes' }
$cfg.wakeOnInput = $wake

if (-not $KeepOff) {
    $keepOffChoice = Ask (T 'After being switched off with the remote, the TV may turn itself back on ~30 s later (HDMI). If its state shows it came back on without anyone using the PC, switch it off again over the network? (same component)' 'Depois de desligada pelo controle, a TV pode religar sozinha ~30 s depois (HDMI). Se o estado dela mostrar que religou sem ninguem usar o PC, desligar de novo pela rede? (mesmo componente)')
} else { $keepOffChoice = $KeepOff -eq 'Yes' }
$cfg.keepOffAfterRemote = $keepOffChoice
Save-TvConfig $cfg

# --------------------------------------------------------------------------- 6b. PC input
# Which TV input the PC is on, so that waking the PC also brings the TV to it (e.g. away from a
# set-top box). Uses the TV's "IP Control" service (port 1516): the remote keys KEY_HDMI1..4 are
# ignored by newer models.
if (-not $PSBoundParameters.ContainsKey('PcInput')) {
    $default = if ($cfg.pcInput) { $cfg.pcInput } else { 'HDMI1' }
    $PcInput = Read-Host ((T '   Which TV input is this PC connected to? (HDMI1-HDMI4, "-" = never switch) [{0}]' '   Em qual entrada da TV este PC esta ligado? (HDMI1-HDMI4, "-" = nunca trocar) [{0}]') -f $default)
    if (-not $PcInput) { $PcInput = $default }
}
$PcInput = $PcInput.Trim().ToUpper()
if ($PcInput -eq '-' -or $PcInput -eq 'NONE') { $PcInput = '' }
if ($PcInput -and $PcInput -notmatch '^HDMI[1-4]$') { Warn ((T 'Unknown input "{0}" - input switching disabled.' 'Entrada desconhecida "{0}" - troca de entrada desativada.') -f $PcInput); $PcInput = '' }
$cfg.pcInput = $PcInput

if ($cfg.pcInput) {
    Step (T 'Input switching (TV "IP Control")...' 'Troca de entrada ("IP Control" da TV)...')
    $ipOk = $false
    if ($cfg.ipControlToken) {
        try { $cur = Get-TvInput $cfg; $ipOk = $true; Ok ((T 'already paired (TV is on {0})' 'ja pareado (TV em {0})') -f $cur) } catch {}
    }
    if (-not $ipOk) {
        if (-not (Test-TvIpControlPort $cfg.ip)) {
            Warn (T 'The TV does not answer on port 1516. Enable it: Settings > All Settings > Connection > Network > Expert Settings > IP Remote = On, then run the installer again. Input switching stays off until then.' 'A TV nao responde na porta 1516. Ative: Configuracoes > Todas > Conexao > Rede > Configuracoes especialistas > IP Remote = Ativado e rode o instalador de novo. A troca de entrada fica desligada ate la.')
            $cfg.ipControlToken = ''
        } else {
            Write-Host (T '   Accept the prompt on the TV...' '   Aceite o aviso na TV...')
            try {
                $cfg.ipControlToken = New-TvIpControlToken $cfg
                $cur = Get-TvInput $cfg
                Ok ((T 'paired (TV is on {0} now)' 'pareado (TV em {0} agora)') -f $cur)
            } catch {
                Warn ((T 'IP Control pairing failed: ' 'O pareamento do IP Control falhou: ') + $_.Exception.Message)
                $cfg.ipControlToken = ''
            }
        }
    }
}
Save-TvConfig $cfg

# --------------------------------------------------------------------------- 7. hooks
Step (T 'Installing...' 'Instalando...')
Set-ShutdownHook
if (Test-ShutdownHook) { Ok (T 'shutdown script registered (turns the TV off)' 'script de desligamento registrado (desliga a TV)') }
else { Warn (T 'shutdown script not visible in the registry yet - it may apply after a reboot' 'script de desligamento ainda nao aparece no registro - pode valer depois de reiniciar') }
Register-PowerOnTask
Ok ((T 'scheduled task "{0}" (turns the TV on at boot, logon and resume)' 'tarefa agendada "{0}" (liga a TV ao iniciar, no login e ao voltar da suspensao)') -f $script:TaskName)

# The helper is always installed now: besides the optional wake on input / keep off (read from
# config.json), it turns the TV off when the PC sleeps and on quickly at resume.
$watcherOk = $false
try {
    Build-Watcher (Join-Path $PSScriptRoot 'Helper')
    Register-WatcherTask
    if (Start-WatcherNow) { $watcherOk = $true; Ok (T 'background component running (TV off on sleep, fast on at resume)' 'componente em segundo plano ativo (TV desliga na suspensao, liga rapido ao voltar)') }
    else { Warn (T 'background component installed but not running yet (starts at next logon)' 'componente em segundo plano instalado, mas ainda nao ativo (inicia no proximo login)') }
} catch {
    Warn ((T 'background component NOT installed - the TV will not turn off on sleep: ' 'componente em segundo plano NAO instalado - a TV nao vai desligar na suspensao: ') + $_.Exception.Message)
}

# --------------------------------------------------------------------------- 8. final check
Step (T 'Checking the installation...' 'Conferindo a instalacao...')
$problems = @()
if (-not (Get-ScheduledTask -TaskName $script:TaskName -ErrorAction SilentlyContinue)) { $problems += (T 'power-on task missing' 'tarefa de ligar ausente') }
if (-not (Get-ScheduledTask -TaskName $script:WatcherTaskName -ErrorAction SilentlyContinue)) { $problems += (T 'helper task missing' 'tarefa do componente ausente') }
if (-not $watcherOk) { $problems += (T 'helper not running' 'componente nao esta rodando') }
if (-not $cfg.token) { $problems += (T 'no pairing token' 'sem token de pareamento') }
if ($cfg.pcInput -and -not $cfg.ipControlToken) { $problems += (T 'input switching not paired' 'troca de entrada nao pareada') }
if ($problems) { Warn ((T 'Problems: ' 'Problemas: ') + ($problems -join '; ')) }
else { Ok (T 'everything in place' 'tudo certo') }

Write-Host ''
Write-Host (T '  Done. Files and log: ' '  Pronto. Arquivos e log: ') -NoNewline; Write-Host $script:InstallDir -ForegroundColor White
Write-Host (T '  Manual control:      ' '  Controle manual:       ') -NoNewline; Write-Host "powershell -File `"$script:InstallDir\CouchCompanion.ps1`" On|Off|Status" -ForegroundColor White
Write-Host (T '  Uninstall:           ' '  Desinstalar:           ') -NoNewline; Write-Host "powershell -File `"$script:InstallDir\Uninstall.ps1`"" -ForegroundColor White
Write-Host (T '  Installer log:       ' '  Log da instalacao:     ') -NoNewline; Write-Host "$script:InstallDir\install.log" -ForegroundColor White
Write-Host (T '  Helper log:          ' '  Log do componente:     ') -NoNewline; Write-Host '%LOCALAPPDATA%\CouchCompanion\watcher.log' -ForegroundColor White
Write-Host ''
try { Stop-Transcript | Out-Null } catch {}
if ($problems) { exit 3 }
exit 0
