# DadBoard Controller

The controller runs on Dad's PC. It reads each PC's status file via SMB and triggers scheduled tasks remotely.

## Run
```powershell
cd controller
python .\DadBoardApp.py
```

## Config
Edit `config.json`:
- `pcs`: list of PC names on the LAN (must match Windows hostnames)
- `shareName`: share name used by clients (`DadBoard$`)
- `timeouts`: used for task creation on client (default values here are informational)
- `staleMinutes`: mark status as stale if last update is older than this

Edit `games.json`:
```json
{ "name": "Deep Rock Galactic", "appid": 548430, "processes": ["FSD.exe"] }
```

## Status interpretation
- OFFLINE: \PC\DadBoard$\status.json not reachable
- STALE: status file is older than `staleMinutes`
- READY: online + Steam running + game process detected

## Notes
- Invites are accepted only when you click the per-PC button.
- If you update `games.json`, re-run `Setup-Client.ps1` on each PC to recreate tasks.
