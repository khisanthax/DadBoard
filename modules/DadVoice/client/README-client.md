# DadVoice Client Setup

## Quick start
1. Copy the `client` folder to the target PC.
2. Run PowerShell as Administrator.
3. Execute:
   `powershell -ExecutionPolicy Bypass -File .\Setup-Client.ps1 -ServerIp 192.168.1.10 -Channel DadVoice`

## Parameters
- `-ServerIp` (required): LAN IP of the Mumble server.
- `-Channel` (default: `DadVoice`): channel path to join (use `/` for subchannels).
- `-Port` (default: `64738`): Mumble server port.
- `-VolumeNormal` (default: `70`): normal volume preset (0-100).
- `-VolumeQuiet` (default: `60`): quiet volume preset (0-100).
- `-VolumeMute` (default: `0`): mute preset (0-100).
- `-CreateShortcuts`: optional desktop shortcut for the Mumble URL.

## What the script does
- Installs Mumble (winget preferred; MSI fallback).
- Copies `assets\mumble.conf.template` to `%APPDATA%\Mumble\mumble.conf`.
- Backs up an existing config once with a timestamp.
- Installs `%ProgramData%\DadVoice\SetVolume.ps1`.
- Creates scheduled tasks: `DadVoice_Start`, `DadVoice_Volume_Normal`, `DadVoice_Volume_Quiet`, `DadVoice_Volume_Mute`.
- Logs actions to `%ProgramData%\DadVoice\setup.log`.

## Notes
- Newer Mumble versions store settings in `mumble_settings.json`. The legacy `mumble.conf` is still accepted for migration; if your version ignores it, open Mumble once and re-check ducking settings.
- The template enables external app ducking to keep voices clear over game audio.
