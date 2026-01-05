# DadVoice requirements (legacy fork)

## Purpose
Provide one-click LAN voice chat setup and remote control across multiple gaming PCs, with voice clarity over game audio.

## Voice stack
- Use Mumble as the voice client on each PC.
- Optionally host a Mumble server via Docker (server module).

## Client setup
- Run a local admin setup script on each PC.
- Install Mumble (winget preferred; MSI fallback).
- Deploy Mumble config from a template that enables application ducking (voice clarity over game audio).
- Back up existing config once.
- Install a local helper script to set master volume.
- Create scheduled tasks (run only when user is logged on):
  - `DadVoice_Start` (opens the Mumble URL)
  - `DadVoice_Volume_Normal`
  - `DadVoice_Volume_Quiet`
  - `DadVoice_Volume_Mute`

## Controller
- Run scripts from the leader PC to trigger the scheduled tasks remotely.
- Support optional credentials for `schtasks /Run`.
- Support a quiet-first flow (apply quiet preset before start).

## Status and troubleshooting
- Log setup actions to `%ProgramData%\DadVoice\setup.log`.
- Document firewall, Task Scheduler, and remote UAC requirements.

## Non-goals
- No always-on monitoring agent.
- No automatic voice activation or AI filtering beyond Mumble and OS audio features.
