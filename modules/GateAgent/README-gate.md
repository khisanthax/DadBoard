# DadBoard GateAgent (Floor Control Gate)

GateAgent is a standalone tray app that gates microphone input for VOX voice chat. It uses simple LAN UDP messages to decide who can transmit, lowering mic level to 5% when a PC is not allowed to talk.

## How it works
- Each PC runs the same GateAgent.exe in the tray.
- Local voice activity (VAD) is detected from the mic input.
- Agents broadcast TALKING and ROLE_CLAIM messages over UDP (port 39555).
- The floor decision is computed locally on every PC (no server).
- If a PC is not allowed, mic level is reduced to 0.05 scalar; otherwise it is restored to the user's normal mic level.

## Roles
- Leader: always allowed to speak.
- Co-Captain: always allowed to speak over Normals.
- Normal: only one Normal is allowed to speak at a time when no Leader/Co-Captain is talking.

## UI
Tray menu:
- Make this PC Leader
- Make this PC Co-Captain
- Set Normal
- Open Status Window

Status window shows:
- Role, Talking, Allowed, mic scalar
- Current floor owner (Normal-only case)
- Peer list (read-only)
- Sensitivity slider + calibrate
- Attack/Release/Lease timing

## Logs and status
- Logs: `%ProgramData%\DadBoardGate\logs\GateAgent.log`
- Status file: `%ProgramData%\DadBoardGate\status.json`

## Build
Publish single-file (recommended):
```powershell
dotnet publish -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=true
```
The output will be under:
`modules\GateAgent\bin\Release\net8.0-windows\win-x64\publish\GateAgent.exe`

## Install
Run as Administrator:
```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Setup-GateAgent.ps1 -SourcePath "C:\path\to\publish"
```
This copies files to `%ProgramData%\DadBoardGate\bin` and creates the scheduled task `DadBoardGateAgent` (start at login).

## Recommended Mumble settings
- VOX enabled (not push-to-talk).
- Moderate VOX threshold so the mic opens only when you're speaking.
- Disable AGC/auto gain if it causes pumping.

## Troubleshooting
- Mic stuck at 5%: exit GateAgent (tray -> Exit) and restart; check log for errors.
- No network peers: GateAgent still works locally; it will allow all when no one is talking.
- Too sensitive: raise the Sensitivity slider.
- Not sensitive enough: lower the Sensitivity slider or run Calibrate in a quiet room.
