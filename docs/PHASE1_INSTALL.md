# Phase 1 Install (Core Spine)

This phase installs the standalone Agent and Leader apps. No voice features are included.

## Build (single-file publish recommended)
From repo root:

```powershell
cd modules\Spine\App
dotnet publish -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=true
```

## Install DadBoard (each PC)
Copy `DadBoard.exe` from the publish folder to the target PC and run it. On first run it will prompt:
- Install (recommended)
- Run without installing

If you choose Install, it prompts for admin rights, installs to `C:\Program Files\DadBoard\`, writes configs under `C:\ProgramData\DadBoard\`, and creates a scheduled task `DadBoardAgent` that runs `DadBoard.exe --mode agent` at logon. After install it relaunches normally.

Optional (legacy): `Setup-DadBoard.ps1` is still available if you prefer a PowerShell installer.

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
