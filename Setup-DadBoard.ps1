param(
    [string]$SourcePath,
    [string]$InstallRoot = "$env:ProgramFiles\DadBoard",
    [string]$TaskName = "DadBoardAgent",
    [string]$RunAsUser = "$env:USERDOMAIN\$env:USERNAME",
    [string]$RunAsPassword,
    [int]$UdpPort = 39555,
    [int]$WsPort = 39601,
    [switch]$CreateLeaderShortcut
)

function Assert-Admin {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Setup-DadBoard.ps1 must be run as Administrator."
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

function Write-AgentConfig {
    param(
        [string]$Path,
        [int]$UdpPort,
        [int]$WsPort
    )

    $pcId = ""
    $displayName = $env:COMPUTERNAME
    $startLeader = $false
    if (Test-Path -Path $Path) {
        try {
            $existing = Get-Content -Path $Path -Raw | ConvertFrom-Json
            if ($existing.pcId) { $pcId = $existing.pcId }
            if ($existing.displayName) { $displayName = $existing.displayName }
            if ($existing.startLeaderOnLogin -ne $null) { $startLeader = [bool]$existing.startLeaderOnLogin }
        } catch { }
    }

    if (-not $pcId) {
        $pcId = [Guid]::NewGuid().ToString("N")
    }

    $config = [pscustomobject]@{
        pcId = $pcId
        displayName = $displayName
        udpPort = $UdpPort
        wsPort = $WsPort
        helloIntervalMs = 1000
        version = "1.0"
        startLeaderOnLogin = $startLeader
    }

    $config | ConvertTo-Json -Depth 4 | Set-Content -Path $Path -Encoding UTF8
}

Assert-Admin

$baseDir = Join-Path $env:ProgramData "DadBoard"
$agentDir = Join-Path $baseDir "Agent"
$leaderDir = Join-Path $baseDir "Leader"
$logDir = Join-Path $baseDir "logs"
$diagDir = Join-Path $baseDir "diag"
New-Item -ItemType Directory -Path $agentDir -Force | Out-Null
New-Item -ItemType Directory -Path $leaderDir -Force | Out-Null
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
New-Item -ItemType Directory -Path $diagDir -Force | Out-Null

if (-not $SourcePath) {
    $default = Join-Path $PSScriptRoot "modules\Spine\App\bin\Release\net8.0-windows\win-x64\publish"
    if (Test-Path -Path $default) {
        $SourcePath = $default
    } else {
        throw "SourcePath is required (folder containing DadBoard.exe)."
    }
}

if (-not (Test-Path -Path $SourcePath)) {
    throw "SourcePath not found: $SourcePath"
}

$exePath = Join-Path $SourcePath "DadBoard.exe"
if (-not (Test-Path -Path $exePath)) {
    throw "DadBoard.exe not found in $SourcePath"
}

Get-ChildItem -Path $SourcePath -File | ForEach-Object {
    Copy-IfDifferent -Source $_.FullName -Destination (Join-Path $InstallRoot $_.Name)
}

$configPath = Join-Path $agentDir "agent.config.json"
Write-AgentConfig -Path $configPath -UdpPort $UdpPort -WsPort $WsPort

$leaderConfigPath = Join-Path $leaderDir "leader.config.json"
$defaultLeaderConfig = Join-Path $PSScriptRoot "modules\Spine\Leader\leader.config.json"
if (-not (Test-Path -Path $leaderConfigPath) -and (Test-Path -Path $defaultLeaderConfig)) {
    Copy-IfDifferent -Source $defaultLeaderConfig -Destination $leaderConfigPath
}

if (-not $RunAsPassword) {
    $cred = Get-Credential -UserName $RunAsUser -Message "Enter password for scheduled task"
    $RunAsPassword = $cred.GetNetworkCredential().Password
}

$null = & icacls $logDir /grant "$RunAsUser:(OI)(CI)M"

$taskAction = Join-Path $InstallRoot "DadBoard.exe"
$taskCommand = "`"$taskAction`" --mode agent"
& schtasks.exe /Create /TN $TaskName /TR $taskCommand /SC ONLOGON /RL LIMITED /F /RU $RunAsUser /RP $RunAsPassword /IT | Out-Null

$startMenu = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\DadBoard"
New-Item -ItemType Directory -Path $startMenu -Force | Out-Null

$shell = New-Object -ComObject WScript.Shell
$trayShortcut = $shell.CreateShortcut((Join-Path $startMenu "DadBoard.lnk"))
$trayShortcut.TargetPath = $taskAction
$trayShortcut.WorkingDirectory = $InstallRoot
$trayShortcut.Save()

if ($CreateLeaderShortcut) {
    $leaderShortcut = $shell.CreateShortcut((Join-Path $startMenu "DadBoard Leader.lnk"))
    $leaderShortcut.TargetPath = $taskAction
    $leaderShortcut.Arguments = "--enable-leader"
    $leaderShortcut.WorkingDirectory = $InstallRoot
    $leaderShortcut.Save()
}

Write-Host "Installed DadBoard to $InstallRoot and created scheduled task $TaskName"
