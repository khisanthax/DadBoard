[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [string]$ServerIp,
  [string]$Channel = "DadVoice",
  [int]$Port = 64738,
  [ValidateRange(0,100)]
  [int]$VolumeNormal = 70,
  [ValidateRange(0,100)]
  [int]$VolumeQuiet = 60,
  [ValidateRange(0,100)]
  [int]$VolumeMute = 0,
  [switch]$CreateShortcuts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Admin {
  $current = [Security.Principal.WindowsIdentity]::GetCurrent()
  $principal = New-Object Security.Principal.WindowsPrincipal($current)
  if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Setup must be run as Administrator."
    exit 1
  }
}

function Write-Log {
  param([string]$Message)
  $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
  $line = "[$timestamp] $Message"
  Add-Content -Path $script:logPath -Value $line
  Write-Host $line
}

function Test-MumbleInstalled {
  $paths = @(
    (Join-Path $env:ProgramFiles "Mumble\\mumble.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Mumble\\mumble.exe")
  ) | Where-Object { $_ -and $_ -ne "" }

  foreach ($path in $paths) {
    if (Test-Path -Path $path) {
      return $true
    }
  }

  return $false
}

function Install-MumbleWinget {
  $winget = Get-Command winget -ErrorAction SilentlyContinue
  if (-not $winget) {
    throw "winget not found."
  }

  $args = @("install","-e","--id","Mumble.Mumble","--silent","--accept-source-agreements","--accept-package-agreements")
  $output = & winget @args 2>&1
  if ($LASTEXITCODE -ne 0) {
    throw "winget install failed: $output"
  }
}

function Install-MumbleFallback {
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

  $apiUrl = "https://api.github.com/repos/mumble-voip/mumble/releases/latest"
  $headers = @{ "User-Agent" = "DadVoice-Setup" }
  $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers
  $asset = $release.assets | Where-Object { $_.name -match 'mumble_client.*x64.*\.msi$' } | Select-Object -First 1
  if (-not $asset) {
    throw "Unable to find a Mumble MSI asset in the latest release."
  }

  $installerPath = Join-Path $env:TEMP $asset.name
  Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $installerPath

  $proc = Start-Process -FilePath "msiexec.exe" -ArgumentList "/i `"$installerPath`" /qn /norestart" -Wait -PassThru
  if ($proc.ExitCode -ne 0) {
    throw "MSI install failed with exit code $($proc.ExitCode)."
  }

  Remove-Item -Path $installerPath -Force -ErrorAction SilentlyContinue
}

function Ensure-Directory {
  param([string]$Path)
  if (-not (Test-Path -Path $Path)) {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
  }
}

function Deploy-MumbleConfig {
  $configDir = Join-Path $env:APPDATA "Mumble"
  $destPath = Join-Path $configDir "mumble.conf"
  $templatePath = Join-Path $PSScriptRoot "assets\\mumble.conf.template"

  if (-not (Test-Path -Path $templatePath)) {
    throw "Missing template: $templatePath"
  }

  Ensure-Directory -Path $configDir

  if (Test-Path -Path $destPath) {
    $existingBackup = Get-ChildItem -Path $configDir -Filter "mumble.conf.bak-*" -ErrorAction SilentlyContinue
    if (-not $existingBackup) {
      $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
      $backupPath = Join-Path $configDir "mumble.conf.bak-$timestamp"
      Copy-Item -Path $destPath -Destination $backupPath -Force
      Write-Log "Backed up existing config to $backupPath"
    }
  }

  Copy-Item -Path $templatePath -Destination $destPath -Force
  Write-Log "Deployed Mumble config to $destPath"
}

function Install-VolumeScript {
  $volumeScriptPath = Join-Path $script:dadVoiceRoot "SetVolume.ps1"
  $scriptContent = @'
param(
  [Parameter(Mandatory)]
  [ValidateRange(0,100)]
  [int]$Volume
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$level = [float]$Volume / 100.0

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume {
    int RegisterControlChangeNotify(IntPtr pNotify);
    int UnregisterControlChangeNotify(IntPtr pNotify);
    int GetChannelCount(out uint pnChannelCount);
    int SetMasterVolumeLevel(float fLevelDB, Guid pguidEventContext);
    int SetMasterVolumeLevelScalar(float fLevel, Guid pguidEventContext);
    int GetMasterVolumeLevel(out float pfLevelDB);
    int GetMasterVolumeLevelScalar(out float pfLevel);
    int SetChannelVolumeLevel(uint nChannel, float fLevelDB, Guid pguidEventContext);
    int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, Guid pguidEventContext);
    int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
    int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, Guid pguidEventContext);
    int GetMute(out bool pbMute);
    int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
    int VolumeStepUp(Guid pguidEventContext);
    int VolumeStepDown(Guid pguidEventContext);
    int QueryHardwareSupport(out uint pdwHardwareSupportMask);
    int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice {
    int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.Interface)] out object ppInterface);
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator {
    int EnumAudioEndpoints(int dataFlow, int dwStateMask, out object ppDevices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    int GetDevice(string pwstrId, out IMMDevice ppDevice);
    int RegisterEndpointNotificationCallback(IntPtr pClient);
    int UnregisterEndpointNotificationCallback(IntPtr pClient);
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumerator {
}

public class Audio {
    public static void SetMasterVolume(float level) {
        var enumerator = new MMDeviceEnumerator() as IMMDeviceEnumerator;
        IMMDevice device;
        int eDataFlow = 0;
        int eRole = 1;

        enumerator.GetDefaultAudioEndpoint(eDataFlow, eRole, out device);

        Guid iid = typeof(IAudioEndpointVolume).GUID;
        object endpointVolumeObj;
        device.Activate(ref iid, 23, IntPtr.Zero, out endpointVolumeObj);
        var endpointVolume = (IAudioEndpointVolume)endpointVolumeObj;
        endpointVolume.SetMasterVolumeLevelScalar(level, Guid.Empty);
    }
}
"@

[Audio]::SetMasterVolume($level)
'@

  Set-Content -Path $volumeScriptPath -Value $scriptContent -Encoding ASCII
  Write-Log "Installed volume helper to $volumeScriptPath"
}

function Build-MumbleUrl {
  param(
    [string]$ServerIp,
    [int]$Port,
    [string]$Channel
  )

  if ([string]::IsNullOrWhiteSpace($Channel)) {
    return "mumble://$ServerIp`:$Port"
  }

  $segments = $Channel -split '/'
  $encoded = ($segments | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/'
  return "mumble://$ServerIp`:$Port/$encoded"
}

function Register-DadVoiceTask {
  param(
    [string]$Name,
    [string]$Command,
    [string]$Arguments,
    [string]$Description
  )

  $principalUser = "$env:USERDOMAIN\\$env:USERNAME"
  $principal = New-ScheduledTaskPrincipal -UserId $principalUser -LogonType InteractiveToken -RunLevel Limited
  $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(20)
  $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
  $action = New-ScheduledTaskAction -Execute $Command -Argument $Arguments

  $task = New-ScheduledTask -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description $Description
  Register-ScheduledTask -TaskName $Name -InputObject $task -Force | Out-Null
  Write-Log "Registered scheduled task: $Name"
}

function Create-UrlShortcut {
  param(
    [string]$Name,
    [string]$Url
  )

  $desktop = [Environment]::GetFolderPath("Desktop")
  if (-not $desktop) {
    Write-Log "Desktop path not found. Skipping shortcut creation."
    return
  }

  $shortcutPath = Join-Path $desktop "$Name.url"
  $content = "[InternetShortcut]`r`nURL=$Url`r`n"
  Set-Content -Path $shortcutPath -Value $content -Encoding ASCII
  Write-Log "Created shortcut: $shortcutPath"
}

Assert-Admin

$script:dadVoiceRoot = Join-Path $env:ProgramData "DadVoice"
Ensure-Directory -Path $script:dadVoiceRoot
$script:logPath = Join-Path $script:dadVoiceRoot "setup.log"

Write-Log "Starting DadVoice client setup."

if (Test-MumbleInstalled) {
  Write-Log "Mumble is already installed."
} else {
  Write-Log "Installing Mumble via winget."
  try {
    Install-MumbleWinget
    Write-Log "Mumble installed via winget."
  } catch {
    Write-Log "Winget install failed. Falling back to direct download."
    Install-MumbleFallback
    Write-Log "Mumble installed via direct download."
  }
}

Deploy-MumbleConfig
Install-VolumeScript

$startUrl = Build-MumbleUrl -ServerIp $ServerIp -Port $Port -Channel $Channel
$psExe = Join-Path $env:SystemRoot "System32\\WindowsPowerShell\\v1.0\\powershell.exe"
$volumeScriptPath = Join-Path $script:dadVoiceRoot "SetVolume.ps1"

Register-DadVoiceTask -Name "DadVoice_Start" -Command "cmd.exe" -Arguments "/c start \"\" `"$startUrl`\"" -Description "Start DadVoice Mumble channel."
Register-DadVoiceTask -Name "DadVoice_Volume_Normal" -Command $psExe -Arguments "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$volumeScriptPath`" -Volume $VolumeNormal" -Description "Set DadVoice volume normal."
Register-DadVoiceTask -Name "DadVoice_Volume_Quiet" -Command $psExe -Arguments "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$volumeScriptPath`" -Volume $VolumeQuiet" -Description "Set DadVoice volume quiet."
Register-DadVoiceTask -Name "DadVoice_Volume_Mute" -Command $psExe -Arguments "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$volumeScriptPath`" -Volume $VolumeMute" -Description "Set DadVoice volume mute."

if ($CreateShortcuts) {
  Create-UrlShortcut -Name "DadVoice" -Url $startUrl
}

Write-Log "DadVoice client setup complete."
