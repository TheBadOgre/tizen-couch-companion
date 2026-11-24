<#
.SYNOPSIS
    Removes Couch Companion: scheduled task, shutdown script entry and installed files.
    Other shutdown scripts in the local group policy are left untouched.
#>
[CmdletBinding()]
param([switch]$KeepFiles)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CouchCompanion.Setup.ps1')

$pt = (Get-UICulture).Name -like 'pt*'
function T([string]$En, [string]$Pt) { if ($script:pt) { $Pt } else { $En } }

if (-not (Test-IsAdmin)) {
    Write-Host (T 'Run as administrator (use Setup.cmd).' 'Execute como administrador (use o Setup.cmd).') -ForegroundColor Red
    exit 1
}

if (Remove-LegacyInstall) { Write-Host (T '   OK  previous version (SamsungTV-Companion) removed' '   OK  versao anterior (SamsungTV-Companion) removida') -ForegroundColor Green }
Unregister-PowerOnTask
Unregister-WatcherTask
Write-Host (T '   OK  scheduled tasks removed' '   OK  tarefas agendadas removidas') -ForegroundColor Green

Set-ShutdownHook -Remove
Write-Host (T '   OK  shutdown script removed' '   OK  script de desligamento removido') -ForegroundColor Green

if (-not $KeepFiles -and (Test-Path -LiteralPath $script:InstallDir)) {
    Set-Location $env:SystemRoot      # in case we are running from inside the folder
    Remove-Item -LiteralPath $script:InstallDir -Recurse -Force
    Write-Host ((T '   OK  {0} deleted' '   OK  {0} apagada') -f $script:InstallDir) -ForegroundColor Green
}

Write-Host ''
Write-Host (T 'Optional: remove this PC from the TV list (Device Connection Manager > Device List).' 'Opcional: tire este PC da lista da TV (Gerenciador de conexao de dispositivos > Lista de dispositivos).')
exit 0
