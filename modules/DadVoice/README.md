# DadVoice

DadVoice is a Windows + Docker setup for one-click family voice chat using Mumble. A controller script on your main PC launches Mumble on all gaming PCs and optionally applies volume presets.

## Repo layout
- `server/` - Docker stack and server docs
- `client/` - Per-PC setup script and Mumble config template
- `controller/` - One-click start and volume presets from the main PC

## Quick start
1. Deploy the Mumble server using `server\README-server.md`.
2. On each gaming PC, run `client\Setup-Client.ps1` as Administrator.
3. On the main PC, edit `shared\config.json` with your PC names and voice task names.
4. Run `controller\Start-DadVoice.ps1` for one-click launch.

## Daily use
- Start voice on all PCs:
  `powershell -ExecutionPolicy Bypass -File .\controller\Start-DadVoice.ps1`
- Set volume preset on all PCs:
  `powershell -ExecutionPolicy Bypass -File .\controller\Set-DadVoiceVolume.ps1 -Preset quiet`

See each subfolder README for details.
