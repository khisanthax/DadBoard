param(
    [string]$SourcePath,
    [string]$InstallRoot = "$env:ProgramData\DadBoard\Leader",
    [string]$ConfigSourcePath,
    [switch]$CreateShortcut
)

function Assert-Admin {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Setup-Leader.ps1 must be run as Administrator."
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

Assert-Admin

$baseDir = Join-Path $env:ProgramData "DadBoard"
$logDir = Join-Path $baseDir "logs"
$diagDir = Join-Path $baseDir "diag"
New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
New-Item -ItemType Directory -Path $diagDir -Force | Out-Null

if (-not $SourcePath) {
    $default = Join-Path $PSScriptRoot "modules\Spine\Leader\bin\Release\net8.0-windows\win-x64\publish"
    if (Test-Path -Path $default) {
        $SourcePath = $default
    } else {
        throw "SourcePath is required (folder containing DadBoard.Leader.exe)."
    }
}

if (-not (Test-Path -Path $SourcePath)) {
    throw "SourcePath not found: $SourcePath"
}

$exePath = Join-Path $SourcePath "DadBoard.Leader.exe"
if (-not (Test-Path -Path $exePath)) {
    throw "DadBoard.Leader.exe not found in $SourcePath"
}

Get-ChildItem -Path $SourcePath -File | ForEach-Object {
    Copy-IfDifferent -Source $_.FullName -Destination (Join-Path $InstallRoot $_.Name)
}

if (-not $ConfigSourcePath) {
    $defaultConfig = Join-Path $PSScriptRoot "modules\Spine\Leader\leader.config.json"
    if (Test-Path -Path $defaultConfig) {
        $ConfigSourcePath = $defaultConfig
    }
}

if ($ConfigSourcePath -and (Test-Path -Path $ConfigSourcePath)) {
    Copy-IfDifferent -Source $ConfigSourcePath -Destination (Join-Path $InstallRoot "leader.config.json")
}

if ($CreateShortcut) {
    $desktop = [Environment]::GetFolderPath("Desktop")
    if ($desktop) {
        $shortcutPath = Join-Path $desktop "DadBoard Leader.lnk"
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = Join-Path $InstallRoot "DadBoard.Leader.exe"
        $shortcut.WorkingDirectory = $InstallRoot
        $shortcut.Save()
    }
}

Write-Host "Installed DadBoard Leader to $InstallRoot"
