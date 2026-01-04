param(
    [string]$InstallRoot = "$env:ProgramData\DadBoard",
    [string]$GamesJsonPath,
    [string]$ShareName = 'DadBoard$',
    [string]$RunAsUser = "$env:USERDOMAIN\$env:USERNAME",
    [string]$RunAsPassword,
    [int]$LaunchTimeoutSec = 120,
    [int]$InviteTimeoutSec = 10
)

function Assert-Admin {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Setup-Client.ps1 must be run as Administrator.'
    }
}

function Copy-IfDifferent {
    param(
        [string]$Source,
        [string]$Destination
    )

    $destDir = Split-Path -Parent $Destination
    if (-not (Test-Path -Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }

    if (Test-Path -Path $Destination) {
        $srcHash = (Get-FileHash -Path $Source -Algorithm SHA256).Hash
        $dstHash = (Get-FileHash -Path $Destination -Algorithm SHA256).Hash
        if ($srcHash -eq $dstHash) { return }
    }

    Copy-Item -Path $Source -Destination $Destination -Force
}

function Ensure-Share {
    param(
        [string]$Name,
        [string]$Path
    )

    $existing = Get-SmbShare -Name $Name -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.Path -ne $Path) {
            throw "Share $Name exists but points to $($existing.Path)."
        }
    } else {
        New-SmbShare -Name $Name -Path $Path -ReadAccess 'BUILTIN\Administrators' -FullAccess 'SYSTEM' | Out-Null
    }

    Revoke-SmbShareAccess -Name $Name -AccountName 'Everyone' -Force -ErrorAction SilentlyContinue | Out-Null
}

function Create-Task {
    param(
        [string]$TaskName,
        [string]$Action,
        [string]$User,
        [string]$Password
    )

    & schtasks.exe /Create /TN $TaskName /TR $Action /SC ONCE /ST 00:00 /RL LIMITED /F /RU $User /RP $Password /IT | Out-Null
}

Assert-Admin

if (-not $GamesJsonPath) {
    $defaultGames = Join-Path (Split-Path -Parent $PSScriptRoot) 'controller\games.json'
    if (Test-Path -Path $defaultGames) {
        $GamesJsonPath = $defaultGames
    } else {
        throw 'GamesJsonPath is required (path to games.json).'
    }
}

if (-not (Test-Path -Path $GamesJsonPath)) {
    throw "games.json not found at $GamesJsonPath"
}

if (-not $RunAsPassword) {
    $cred = Get-Credential -UserName $RunAsUser -Message 'Enter password for scheduled tasks'
    $RunAsPassword = $cred.GetNetworkCredential().Password
}

$actionsDir = Join-Path $InstallRoot 'actions'
$logsDir = Join-Path $InstallRoot 'logs'
New-Item -ItemType Directory -Path $actionsDir -Force | Out-Null
New-Item -ItemType Directory -Path $logsDir -Force | Out-Null

$scriptDir = Join-Path $PSScriptRoot 'actions'
Copy-IfDifferent -Source (Join-Path $scriptDir 'WriteStatus.ps1') -Destination (Join-Path $actionsDir 'WriteStatus.ps1')
Copy-IfDifferent -Source (Join-Path $scriptDir 'DetectProcesses.ps1') -Destination (Join-Path $actionsDir 'DetectProcesses.ps1')
Copy-IfDifferent -Source (Join-Path $scriptDir 'LaunchGame.ps1') -Destination (Join-Path $actionsDir 'LaunchGame.ps1')
Copy-IfDifferent -Source (Join-Path $scriptDir 'AcceptInvite.ps1') -Destination (Join-Path $actionsDir 'AcceptInvite.ps1')
Copy-IfDifferent -Source $GamesJsonPath -Destination (Join-Path $InstallRoot 'games.json')

. (Join-Path $actionsDir 'WriteStatus.ps1')
$initStatus = Get-DadBoardStatus -Path (Join-Path $InstallRoot 'status.json')
Write-DadBoardStatus -Status $initStatus -Path (Join-Path $InstallRoot 'status.json')

Ensure-Share -Name $ShareName -Path $InstallRoot

$games = Get-Content -Path $GamesJsonPath -Raw | ConvertFrom-Json
$statusPath = Join-Path $InstallRoot 'status.json'

$launchScript = Join-Path $actionsDir 'LaunchGame.ps1'
$acceptScript = Join-Path $actionsDir 'AcceptInvite.ps1'

foreach ($game in $games) {
    $appid = $game.appid
    $name = $game.name
    $processCsv = ''
    if ($game.processes) { $processCsv = ($game.processes -join ',') }

    $taskName = "DadBoard_LaunchGame_$appid"
    $action = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$launchScript`" -AppId $appid -GameName `"$name`" -ProcessesCsv `"$processCsv`" -LaunchTimeoutSec $LaunchTimeoutSec -StatusPath `"$statusPath`""
    Create-Task -TaskName $taskName -Action $action -User $RunAsUser -Password $RunAsPassword
}

$acceptTask = 'DadBoard_AcceptInvite'
$acceptAction = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$acceptScript`" -InviteTimeoutSec $InviteTimeoutSec -StatusPath `"$statusPath`""
Create-Task -TaskName $acceptTask -Action $acceptAction -User $RunAsUser -Password $RunAsPassword

Write-Host "Setup complete. Share: \\$env:COMPUTERNAME\$ShareName"
