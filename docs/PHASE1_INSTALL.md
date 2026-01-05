# Phase 1 Install (Core Spine)

This phase installs the standalone Agent and Leader apps. No voice features are included.

## Build (single-file publish recommended)
From repo root:

```powershell
cd modules\Spine\Agent
dotnet publish -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=true

cd ..\Leader
dotnet publish -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=true
```

## Install Agent (each PC)
Copy the repo folder (or just the published output) to the target PC and run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Setup-Agent.ps1
```

This installs to `C:\ProgramData\DadBoard\Agent\`, writes `agent.config.json`, and creates the scheduled task `DadBoardAgent` at logon.

## Install Leader (leader PC)
```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Setup-Leader.ps1 -CreateShortcut
```

This installs to `C:\ProgramData\DadBoard\Leader\` and copies `leader.config.json`.

## Verify running
- Agent log: `C:\ProgramData\DadBoard\logs\agent.log`
- Leader log: `C:\ProgramData\DadBoard\logs\leader.log`
- Agent state: `C:\ProgramData\DadBoard\Agent\agent_state.json`
- Leader known agents: `C:\ProgramData\DadBoard\known_agents.json`

## Run diagnostics
```powershell
.\tools\diag_bundle.ps1
```
Output is written to `C:\ProgramData\DadBoard\diag\dadboard_diag_YYYYMMDD_HHMMSS.txt`.
