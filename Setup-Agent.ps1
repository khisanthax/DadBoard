param(
    [string]$SourcePath,
    [string]$InstallRoot = "$env:ProgramData\DadBoard\Agent",
    [string]$TaskName = "DadBoardAgent",
    [int]$UdpPort = 39555,
    [int]$WsPort = 39601,
    [string]$RunAsUser = "$env:USERDOMAIN\$env:USERNAME",
    [string]$RunAsPassword
)

function Assert-Admin {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Setup-Agent.ps1 must be run as Administrator."
    }
}

function Write-Config {
    param(
        [string]$Path,
        [int]$UdpPort,
        [int]$WsPort
    )

    $pcId = ""
    $displayName = $env:COMPUTERNAME
    if (Test-Path -Path $Path) {
        try {
            $existing = Get-Content -Path $Path -Raw | ConvertFrom-Json
            if ($existing.pcId) { $pcId = $existing.pcId }
            if ($existing.displayName) { $displayName = $existing.displayName }
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
    }

    $config | ConvertTo-Json -Depth 4 | Set-Content -Path $Path -Encoding UTF8
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

Assert-Admin

$baseDir = Join-Path $env:ProgramData "DadBoard"
$logDir = Join-Path $baseDir "logs"
$diagDir = Join-Path $baseDir "diag"
New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
New-Item -ItemType Directory -Path $diagDir -Force | Out-Null

if (-not $SourcePath) {
    $default = Join-Path $PSScriptRoot "modules\Spine\Agent\bin\Release\net8.0-windows\win-x64\publish"
    if (Test-Path -Path $default) {
        $SourcePath = $default
    } else {
        throw "SourcePath is required (folder containing DadBoard.Agent.exe)."
    }
}

if (-not (Test-Path -Path $SourcePath)) {
    throw "SourcePath not found: $SourcePath"
}

$exePath = Join-Path $SourcePath "DadBoard.Agent.exe"
if (-not (Test-Path -Path $exePath)) {
    throw "DadBoard.Agent.exe not found in $SourcePath"
}

Get-ChildItem -Path $SourcePath -File | ForEach-Object {
    Copy-IfDifferent -Source $_.FullName -Destination (Join-Path $InstallRoot $_.Name)
}

$configPath = Join-Path $InstallRoot "agent.config.json"
Write-Config -Path $configPath -UdpPort $UdpPort -WsPort $WsPort

if (-not $RunAsPassword) {
    $cred = Get-Credential -UserName $RunAsUser -Message "Enter password for scheduled task"
    $RunAsPassword = $cred.GetNetworkCredential().Password
}

$taskAction = Join-Path $InstallRoot "DadBoard.Agent.exe"
$taskCommand = "`"$taskAction`""
& schtasks.exe /Create /TN $TaskName /TR $taskCommand /SC ONLOGON /RL LIMITED /F /RU $RunAsUser /RP $RunAsPassword /IT | Out-Null

Write-Host "Installed DadBoard Agent to $InstallRoot and created task $TaskName"
