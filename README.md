# DadBoard

DadBoard is a simple Windows LAN dashboard to launch a selected Steam game on multiple PCs and help Dad coordinate invites/accepts. It uses scheduled tasks and per-PC status files shared via hidden SMB shares.

## Repo layout
- client/: setup script and local action scripts run by scheduled tasks
- controller/: Tkinter dashboard app + config and game list

## Quick start
1) Edit `shared/config.json` with PC names and voice task names.
2) Edit `controller/games.json` with your Steam AppIDs and game process names.
3) Run `client/Setup-Client.ps1` locally on each PC (as admin).
4) On Dad PC, run `python controller/DadBoardApp.py`.

See `client/README-client.md` and `controller/README-controller.md` for details.
