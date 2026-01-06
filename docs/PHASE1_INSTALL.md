# Phase 1 Install (Core Spine)

This phase installs the standalone Agent and Leader apps. No voice features are included.

## Build (single-file publish recommended)
From repo root:

```powershell
cd modules\Spine\App
dotnet publish -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=true
```

## Install DadBoard (each PC)
Copy the repo folder (or just the published output) to the target PC and run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Setup-DadBoard.ps1 -CreateLeaderShortcut
```

This installs `DadBoard.exe` to `C:\Program Files\DadBoard\`, writes `agent.config.json` and `leader.config.json` under `C:\ProgramData\DadBoard\`, and creates a scheduled task `DadBoard` at logon. The optional Start Menu shortcut `DadBoard Leader` launches the app with leader enabled.

## Verify running
- Agent log: `C:\ProgramData\DadBoard\logs\agent.log`
- Leader log (if enabled): `C:\ProgramData\DadBoard\logs\leader.log`
- Agent state: `C:\ProgramData\DadBoard\Agent\agent_state.json`
- Leader known agents (if enabled): `C:\ProgramData\DadBoard\known_agents.json`

## Run diagnostics
```powershell
.\tools\diag_bundle.ps1
```
Output is written to `C:\ProgramData\DadBoard\diag\dadboard_diag_YYYYMMDD_HHMMSS.txt`.
