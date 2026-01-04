# DadBoard Client Setup

Run this on each gaming PC (including Dad PC if you want local status tracking). This script installs the action scripts, creates the SMB share, and registers scheduled tasks.

## Prereqs
- Windows 10/11
- PowerShell 5.1+
- Run as Administrator
- Steam installed and logged in

## Setup
1) Copy the repo (or at least `client/`) onto the target PC.
2) Edit `controller/games.json` on the Dad PC, then copy it to the target PC.
3) Run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Setup-Client.ps1 -GamesJsonPath "C:\path\to\games.json"
```

You will be prompted for the password of the account that should run the tasks. Use the account that will be logged in during play.

## What it changes
- Creates `%ProgramData%\DadBoard\` and copies scripts into `%ProgramData%\DadBoard\actions`
- Creates hidden share `DadBoard$` for `%ProgramData%\DadBoard` (read for Administrators)
- Creates scheduled tasks:
  - `DadBoard_LaunchGame_<AppId>` (one per game)
  - `DadBoard_AcceptInvite`
- Initializes `%ProgramData%\DadBoard\status.json`

## Status file
The local status file is always written to:
- `%ProgramData%\DadBoard\status.json`

## Troubleshooting
- If status is stale, verify the PC can write `%ProgramData%\DadBoard\status.json` and that tasks run for the logged-in user.
- If invites cannot be accepted, keep Steam open to the chat list and ensure the game is running on that PC.
- Check logs in `%ProgramData%\DadBoard\logs\` for action output.
