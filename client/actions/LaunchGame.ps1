param(
    [Parameter(Mandatory=$true)][int]$AppId,
    [Parameter(Mandatory=$true)][string]$GameName,
    [string[]]$Processes,
    [string]$ProcessesCsv,
    [int]$LaunchTimeoutSec = 120,
    [string]$StatusPath = "$env:ProgramData\DadBoard\status.json"
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. "$scriptRoot\WriteStatus.ps1"
. "$scriptRoot\DetectProcesses.ps1"

$logDir = "$env:ProgramData\DadBoard\logs"
if (-not (Test-Path -Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$logPath = Join-Path $logDir 'LaunchGame.log'

function Write-Log {
    param([string]$Message)
    $ts = (Get-Date).ToString('o')
    Add-Content -Path $logPath -Value "$ts $Message"
}

try {
    if (-not $Processes -or $Processes.Count -eq 0) {
        if ($ProcessesCsv) {
            $Processes = $ProcessesCsv -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
        } else {
            $Processes = @()
        }
    }

    $status = Get-DadBoardStatus -Path $StatusPath
    $status.steam.running = Test-AnyProcessRunning -Names @('steam')
    $status.game.appid = $AppId
    $status.game.name = $GameName
    $status.game.processes = $Processes
    $status.game.lastLaunch = (Get-Date).ToString('o')
    $status.game.launchResult = 'STARTED'
    $status.game.running = $false
    Write-DadBoardStatus -Status $status -Path $StatusPath

    Write-Log "Launching appid=$AppId name=$GameName"
    Start-Process "steam://run/$AppId" | Out-Null

    $start = Get-Date
    $running = $false
    while ((Get-Date) - $start -lt [TimeSpan]::FromSeconds($LaunchTimeoutSec)) {
        if (Test-AnyProcessRunning -Names $Processes) {
            $running = $true
            break
        }
        Start-Sleep -Milliseconds 500
    }

    $status = Get-DadBoardStatus -Path $StatusPath
    $status.steam.running = Test-AnyProcessRunning -Names @('steam')
    $status.game.running = $running
    $status.game.launchResult = if ($running) { 'SUCCESS' } else { 'TIMEOUT' }
    Write-DadBoardStatus -Status $status -Path $StatusPath

    Write-Log "Launch result=$($status.game.launchResult) running=$running"
} catch {
    $status = Get-DadBoardStatus -Path $StatusPath
    $status.steam.running = Test-AnyProcessRunning -Names @('steam')
    $status.game.running = $false
    $status.game.launchResult = 'ERROR'
    Write-DadBoardStatus -Status $status -Path $StatusPath

    Write-Log "ERROR: $($_.Exception.Message)"
    throw
}
