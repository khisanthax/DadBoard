param(
    [string]$SourcePath,
    [string]$InstallRoot = "$env:ProgramData\DadBoardGate",
    [string]$TaskName = "DadBoardGateAgent",
    [string]$RunAsUser = "$env:USERDOMAIN\$env:USERNAME",
    [string]$RunAsPassword
)

function Assert-Admin {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Setup-GateAgent.ps1 must be run as Administrator."
    }
}

Assert-Admin

if (-not $SourcePath) {
    $defaultPath = Join-Path $PSScriptRoot "bin\Release\net8.0-windows\win-x64\publish"
    if (Test-Path -Path $defaultPath) {
        $SourcePath = $defaultPath
    } else {
        throw "SourcePath is required (folder containing GateAgent.exe)."
    }
}

if (-not (Test-Path -Path $SourcePath)) {
    throw "SourcePath not found: $SourcePath"
}

$exePath = Join-Path $SourcePath "GateAgent.exe"
if (-not (Test-Path -Path $exePath)) {
    throw "GateAgent.exe not found in $SourcePath"
}

if (-not $RunAsPassword) {
    $cred = Get-Credential -UserName $RunAsUser -Message "Enter password for scheduled task"
    $RunAsPassword = $cred.GetNetworkCredential().Password
}

$binDir = Join-Path $InstallRoot "bin"
$logsDir = Join-Path $InstallRoot "logs"
New-Item -ItemType Directory -Path $binDir -Force | Out-Null
New-Item -ItemType Directory -Path $logsDir -Force | Out-Null

Copy-Item -Path (Join-Path $SourcePath "*") -Destination $binDir -Recurse -Force

$taskAction = Join-Path $binDir "GateAgent.exe"
$taskCommand = \"`\"$taskAction`\"\"
& schtasks.exe /Create /TN $TaskName /TR $taskCommand /SC ONLOGON /RL LIMITED /F /RU $RunAsUser /RP $RunAsPassword /IT | Out-Null

Write-Host "Installed GateAgent to $binDir and created task $TaskName"
